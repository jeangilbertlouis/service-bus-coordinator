# Service Bus Coordinator — Design & Plan

A cross-platform desktop app (Windows, macOS, Linux) for exploring and operating on
Azure Service Bus namespaces.

For building, testing and extending the app, see [DEVELOPMENT.md](DEVELOPMENT.md).

## 1. Goals

| Requirement | Summary |
|---|---|
| Cross-platform | Native desktop app on Windows, macOS and Linux |
| Multiple namespaces | Connect to and browse many Service Bus namespaces side by side |
| Connection strings | Connect using a Service Bus connection string |
| View messages | Peek (non-destructive) messages on queues, subscriptions and their dead-letter queues |
| Purge | Delete all messages from an entity (active or dead-letter) |
| DLQ resubmit | Move dead-lettered messages back to the main entity |

## 2. Decisions

| # | Decision | Choice |
|---|---|---|
| 1 | Tech stack | **.NET 10 + Avalonia UI**, MVVM via CommunityToolkit.Mvvm, `Azure.Messaging.ServiceBus` SDK |
| 2 | Connection string storage | **OS secure storage**: Windows DPAPI, macOS Keychain, Linux libsecret (via `Microsoft.Identity.Client.Extensions.Msal` storage) |
| 3 | Permissions | Entity discovery requires **Manage** rights. If listing fails (Listen/Send-only, or an entity-scoped connection string), the user can **add entities manually by name** |
| 4 | DLQ resubmit | Selected / all / filtered messages; **transactional** move (send + complete atomically) with a safe fallback; **warn** before resubmitting a subscription DLQ to its topic (fan-out to all subscriptions); allow choosing a different target entity; option to strip or keep dead-letter metadata and stamp an `x-resubmitted-*` header |
| 5 | Edge cases | Handled in phases (see §6): sessions, transfer DLQ, scheduled/deferred messages, throttling and progress on bulk operations |

## 3. Architecture

```
ServiceBusCoordinator.sln
├── src/
│   ├── ServiceBusCoordinator.Core        (net10.0 class library — no UI)
│   │   ├── Connections/   connection model, parsing, secure store
│   │   ├── Explorer/      entity discovery + runtime counts (admin client)
│   │   ├── Messages/      peek, purge, resubmit operations
│   │   └── Models/        entity + message view models (plain records)
│   └── ServiceBusCoordinator.App         (Avalonia desktop app)
│       ├── ViewModels/    MVVM view models (CommunityToolkit.Mvvm)
│       └── Views/         XAML views
└── tests/
    └── ServiceBusCoordinator.Core.Tests  (xUnit)
```

* **Core** owns all Azure SDK interaction and is UI-agnostic, so it can later back a CLI or web front end.
* **App** is a thin MVVM layer. Long-running operations take a `CancellationToken` and report progress via `IProgress<T>`.
* One `ServiceBusClient` + `ServiceBusAdministrationClient` pair is cached per connected namespace.

### 3.1 Connection management
* A connection = display name + connection string (+ optional manually added entities).
* Parsed with `ServiceBusConnectionStringProperties` to show the namespace host and detect an `EntityPath`.
* All saved connections are serialised to JSON and stored as a single encrypted blob in the OS secret store.
  Nothing secret is ever written to disk in plain text.
* **Folders.** Connections can be grouped in sidebar folders (one level deep), created with **New folder** on the
  Namespaces header or the tree's context menu, renamed from the context menu or the folder's overview. Deleting a
  folder keeps its connections and moves them to the top level where the folder was. A new connection goes into the
  selected folder (or the selected connection's folder), otherwise to the end of the list.
* **Drag and drop.** Connections and folders are dragged with the left button (a 6px move starts the drag, so a click
  still just selects). Dropping on the top or bottom half of a row places the item before or after it, shown by a
  line; dropping a connection on the middle of a folder files it inside, shown by an outline. Folders stay at the top
  level. Escape cancels, and the tree scrolls when dragging near its edges. The drag is handled with pointer events
  in `MainWindow.axaml.cs` (no OS drag-and-drop), and `MainWindowViewModel.ResolveDrop` / `Move` decide and apply it.
* The blob (format version 2) holds the connections in display order, each with an optional `FolderId`, plus the
  folders (name, expanded) and the order of top-level entries (`ConnectionLayout` in Core). Version 1 blobs load with
  every connection at the top level.

### 3.2 Entity discovery
* **Connections connect lazily** (unless *Connect at startup* is turned on in Options, §3.16). At startup every saved
  connection is shown collapsed and nothing is contacted. A
  connection connects and lists its entities the first time it is expanded (or via **Connect** on its overview,
  Refresh, or Add entity). Until then it holds a "Connecting…" placeholder child so the tree shows an expander. A
  failed attempt leaves a "Could not connect" placeholder; expanding again or **Try again** retries. A newly added
  connection follows the same rule: it is added collapsed and selected, and connects when expanded or on **Connect**.
  This keeps startup fast and quiet with many connections.
* Each connection groups entities under **Queues** and **Topics** folders. A folder's pills show the total active and
  dead-letter messages of the queues or subscriptions in it (not how many entities it holds); they update when an
  entity is refreshed or purged. Each topic row likewise shows its subscriptions' active and dead-letter totals (the
  subscription count is in the pill's tooltip). A queue whose name ends in `_error` (an error-queue convention, configurable in
  Options) shows its message count in red while it holds messages.
* A connection's overview shows tiles for its entities, total **active** messages (queues and subscriptions),
  total **dead-lettered**, and messages **in error queues** with how many error queues there are (hidden when
  error-queue highlighting is off). Error-queue messages are also counted in the active total. By default Queues starts expanded and Topics, and each topic, collapsed (Options);
  Refresh keeps whatever the user has expanded or collapsed since.
* `ServiceBusAdministrationClient.GetQueuesAsync / GetTopicsAsync / GetSubscriptionsAsync`
  plus `Get*RuntimePropertiesAsync` for active, dead-letter, scheduled and transfer-DLQ counts.
* On `UnauthorizedAccessException` (no Manage claim) the namespace shows a "limited access" state
  and the user can add a queue or `topic/subscriptions/name` manually. Counts are unavailable in that mode.
* If the connection string contains `EntityPath`, that entity is added automatically.

### 3.3 Viewing messages
* `ServiceBusReceiver.PeekMessagesAsync` — never locks or removes messages.
* Paged by sequence number ("Load more"); page size configurable (default 50).
* Sub-queue picker: **Active** / **Dead-letter** / **Transfer dead-letter**.
* A **State** column marks *Scheduled* and *Deferred* messages (peek returns both).
* Detail pane: body (pretty-printed JSON/XML, text, or hex dump for binary), system properties
  (MessageId, SequenceNumber, EnqueuedTime, DeliveryCount, SessionId, ContentType, CorrelationId,
  TTL, DeadLetterReason/ErrorDescription/Source) and application properties.

### 3.4 Purge
* Confirmation dialog requiring the entity name to be entered for large purges (more than 100 messages by default,
  configurable in Options, or an unknown count). The name is shown with a **Copy** button so it can be pasted;
  surrounding whitespace from a paste is ignored.
* Drains the entity with a `ReceiveAndDelete` receiver in batches of 500; progress is reported as messages deleted. Cancellable.
* Stops when the entity is empty or once it reaches messages enqueued after the purge started, so a busy
  entity cannot keep it looping forever.
* Then sweeps (by peeking) for **deferred** messages — deleted via `ReceiveDeferredMessagesAsync` — and, on queues,
  **scheduled** messages — cancelled via `CancelScheduledMessagesAsync`. Both are invisible to a normal receive.
* Session-enabled entities are purged session by session (see §3.6).
* The SDK's server-side batch delete (`DeleteMessagesAsync`) is still internal in `Azure.Messaging.ServiceBus` 7.21;
  switch to it when it becomes public (much faster for large entities).

### 3.5 DLQ resubmit
1. Receive from the DLQ in PeekLock mode (all, or only the selected sequence numbers).
2. Build a new `ServiceBusMessage` from the received message (body, properties, application properties, SessionId, etc.).
3. Optionally strip `DeadLetterReason` / `DeadLetterErrorDescription` / `DeadLetterSource`
   application properties, and add `x-resubmitted-at` / `x-resubmitted-from`.
4. **Transactional**: send to the target + complete in the DLQ inside a `TransactionScope` on a client
   created with `EnableCrossEntityTransactions = true`.
5. **Fallback** (if the transaction is rejected): send, then complete. At-least-once — a crash between the
   two steps can produce a duplicate but never loses a message.
6. **Selected messages**: the DLQ cannot be read by sequence number, so the receiver walks the DLQ and keeps the
   lock on non-selected messages (abandoning them at the end) until every selected message is found. On very large
   DLQs, locks on skipped messages may expire (default 60 s); that is harmless — they are simply re-seen and skipped.
7. **All messages**: capped at the DLQ count read at the start, so messages that are dead-lettered again during
   the run are not picked up in an endless loop.
8. Target defaults to the parent entity (queue, or the subscription's **topic**). Resubmitting to a topic delivers
   the message to **every** matching subscription — the UI warns about this and offers another target entity instead.
9. Works the same from the **transfer dead-letter queue** (`/$transfer/$deadletterqueue`).

### 3.6 Session-enabled entities (phase 4)
* Dead-letter sub-queues never require sessions, so DLQ browsing, purge and resubmit are unchanged.
* **Active messages** are browsed one session at a time: type a session ID, take the **next available** session,
  or **Find sessions**. Service Bus has no API to list sessions without locking them, so *Find sessions* accepts
  sessions one after another (holding each briefly), then releases them all. Consumers of those sessions pause for
  those few seconds — the UI says so.
* **Session state** is shown for the open session and can be cleared (with confirmation).
* **Purge** accepts each next available session in ReceiveAndDelete mode, drains it and removes its deferred
  messages, until no session is offered. Limitation: a session that holds *only* scheduled or deferred messages is
  never offered by the service, so it can't be reached without its session ID.

### 3.7 Scheduled & deferred messages (phase 4)
* **Cancel scheduled**: tick scheduled messages on a queue and cancel them. Scheduled messages on a *topic* are
  held by the topic itself and can't be peeked through a subscription, so they are not shown.
* **Delete deferred**: tick deferred messages and delete them (receive-by-sequence-number in ReceiveAndDelete mode).
  On session entities this uses the open session.

### 3.8 Throttling (phase 4)
* Every client uses exponential retry (6 retries, 0.8 s → 30 s, 60 s per try).
* Bulk loops (purge, resubmit, cancel) add another layer: if the namespace is still `ServiceBusy` after the SDK
  retries, they wait 2 s, 4 s, 8 s … (max 60 s, 8 attempts) and carry on, showing "namespace is throttling — waiting Ns".

### 3.9 Emulator support
* A connection string with `UseDevelopmentEmulator=true` is accepted as-is. The emulator serves management
  operations on port **5300**, so the app adds that port for the administration client automatically.

### 3.10 Search & filter (phase 5)
* A filter box above the message list narrows the **loaded** messages as you type (client-side; "Load more"
  results are filtered too). Matches message ID, subject, correlation/session ID, content type, dead-letter
  reason/description, application property names and values, and the body text (case-insensitive).
* `name=value` matches only application property `name` containing `value`; a number also matches a sequence number.
* Ticked messages stay ticked when hidden by the filter, so resubmit/export of "ticked" includes them.

### 3.11 Export (phase 5)
* **Export…** saves the ticked messages — or, if none are ticked, every message currently shown — to a JSON file.
* **Export all…** peeks the whole sub-queue (or the open session) page by page, up to 10,000 messages, then saves it.
* Format (`formatVersion: 1`): `{ source, exportedAt, messages: [...] }`; each message has its system properties,
  `applicationProperties`, and `body` as UTF-8 text (`bodyEncoding: "utf-8"`) or base64 (`"base64"`) for binary.
  Unset fields are omitted.

### 3.12 Send & edit-and-resend (phase 5)
* **Send…** composes a new message: target (defaults to the queue, or the subscription's topic), body, content type,
  subject, message/correlation/session ID, To, ReplyTo, time to live, an optional schedule (minutes from now), and
  application properties as `name=value` lines (typed: `true/false`, whole numbers, decimals, `"quoted"` strings).
* **Edit & resend…** pre-fills the dialog from the selected message, with a **new message ID** (so duplicate detection
  doesn't drop it) and without the dead-letter reason properties.
* A blank message ID gets a generated GUID (the SDK rejects an empty one).

### 3.13 Packaging & CI (phase 5)
* `build/publish.ps1` (Windows) and `build/publish.sh` (macOS/Linux) publish a **self-contained single-file** app
  per runtime and strip the ~100 MB of native debug symbols:
  * `win-x64` → `.zip` with `ServiceBusCoordinator.exe` (~45 MB)
  * `linux-x64` → `.tar.gz` with the `ServiceBusCoordinator` executable (~44 MB)
  * `osx-arm64` / `osx-x64` → `.zip` with `Service Bus Coordinator.app` (bundle built from `build/macos/Info.plist`)
* `.github/workflows/ci.yml` tests and packages on Windows, Linux and macOS for every push and PR, uploading the
  packages as workflow artifacts. Pushing a `v*` tag (e.g. `v0.5.0`) also creates a GitHub release with them attached.
* Packages are **unsigned**: Windows SmartScreen and macOS Gatekeeper will warn on first launch (on macOS run
  `xattr -dr com.apple.quarantine "Service Bus Coordinator.app"` or right-click → Open). Signed installers are a
  future improvement.

### 3.14a In-place updates

* On startup (and from **Check for updates** in the status bar) the app asks GitHub for the latest stable release
  (`/repos/jeangilbertlouis/service-bus-coordinator/releases/latest`; drafts and pre-releases are never offered) and
  compares its tag with the running version by SemVer precedence. A newer release shows a banner in the sidebar.
  Nothing else is sent: the only identifying header is `User-Agent: ServiceBusCoordinator/<version>`. Set
  `SBC_DISABLE_UPDATE_CHECK=1`, or untick *Check for updates when the app starts* in Options, to skip the startup check.
* **Update and restart** downloads the package for the running RID next to the install, checks its SHA-256 against
  the release's `SHA256SUMS.txt` (written by CI), unpacks it, then **renames** the running executable (or `.app`
  bundle) to `*.old` and moves the new one into its place. Renaming a running file works on Windows, macOS and
  Linux, so no helper process is needed. If the second move fails the first is rolled back. The new version is
  started (`open -n` for a bundle) and the old process exits; the next start deletes `*.old` and any leftover
  `.sbc-update-*` staging folder.
* Integrity rests on HTTPS to GitHub plus the checksum file from the same release, which catches corrupt or truncated
  downloads but not a compromised release. Code signing is the stronger fix (see Future improvements).
* Only published single-file builds update themselves (detected by an empty assembly location). Running from source,
  a release without a package or checksum for this platform, or an install folder the user cannot write to fall back
  to opening the release page.
* Logic lives in `Core/Updates` (`SemanticVersion`, `UpdateChecker`, `UpdateInstaller`) and is unit-tested with
  synthetic packages; `App/Services/UpdateService` wires it to the UI.

### 3.14 Entity properties (post-phase 5)
* Every queue, topic and subscription has a **Properties** tab (topics are now selectable and show only this tab).
* Read-only, grouped into cards: *General* (status, created/updated/accessed, size, max size, max message size,
  partitioning, batching, user metadata), *Message counts*, *Message handling* (lock duration, max delivery count,
  default TTL, sessions, duplicate detection + window, auto-delete on idle), *Dead-lettering & forwarding*, and
  *Shared access policies* (names and rights only — keys are never shown).
* Subscriptions also list their **rules**: filter kind (SQL / Correlation / All / None), the filter in readable form,
  and the SQL action if any.
* "Never" is shown for `TimeSpan.MaxValue`; sizes and durations are humanised.
* Needs Manage rights; otherwise the tab explains that.

### 3.15 Visual design
* Fluent theme with an Azure-blue accent and a matching set of light/dark brushes (`App.axaml` theme dictionaries),
  following the OS light/dark setting.
* Sidebar with app header, primary *Add connection* button, icon buttons, and a tree with per-kind icons and colours
  (queue blue, topic purple, subscription teal), count pills and red *DLQ n* pills; namespaces show their host,
  an *emulator* / *limited* / *error* pill, and the namespace-wide dead-letter total.
* Entity header with icon, kind and session pills, full path, and stat tiles (the dead-letter tile turns red when
  non-zero). Toolbar actions are grouped with dividers and icons; the message list is a card with a header row and
  zebra rows; a status bar shows progress and the app version.
* Icons are small vector paths in `Icons.cs` (no icon font or image assets).

### 3.16 Options
* **Options** (gear button in the sidebar toolbar, or the status bar link) holds the default behaviours: connect all
  connections at startup (off), which of Queues / Topics / each topic start expanded (Queues only), the purge size
  that asks for the entity name (100), checking for updates at startup (on), and red counts for error queues (on,
  names ending in `_error`; several comma-separated endings allowed, matched case-insensitively).
* Stored as plain JSON (nothing secret) in `options.json` under the per-user application-data folder
  (`%APPDATA%\ServiceBusCoordinator` on Windows, `~/.config/ServiceBusCoordinator` on Linux and macOS). A missing or
  unreadable file means the defaults; unknown properties are ignored, so older and newer versions can share it.
* `Core/Settings/AppOptions` is the model and store; one shared instance is handed to every connection, so tree
  options apply the next time a connection loads.

## 4. Security
* Connection strings only live in memory and in the OS secret store.
* Connection strings are never logged; error messages are scrubbed of `SharedAccessKey=…`.
* Destructive operations (purge, resubmit) always require explicit confirmation.

## 5. Testing
* **Unit tests** (xUnit) for connection parsing, emulator port handling, message cloning/stripping, secret scrubbing,
  store serialisation, sub-queue mapping and throttling back-off.
* **Integration tests** (`IntegrationTests.cs`) run only when `SBC_TEST_CONNECTION` is set to a connection string with
  Manage rights — the local emulator (`emulator/`) or a disposable dev namespace. Each test creates and deletes its own
  queue and covers discovery, resubmit (all/selected), purge incl. deferred & scheduled, cancel/delete by sequence
  number, and sessions (find, peek, purge).

## 6. Delivery phases

| Phase | Scope | Status |
|---|---|---|
| 1. Foundation | Solution skeleton, connection manager with OS secure storage, namespace tree with counts, manual-entity fallback | Built — needs live-namespace testing |
| 2. Viewer | Peek with paging, Active/DLQ toggle, message detail pane | Built — needs live-namespace testing |
| 3. Operations | Purge (confirm + progress + cancel), DLQ resubmit (selected/all, transactional, topic warning, target picker) | Built — needs live-namespace testing |
| 4. Edge cases | Session-enabled entities (peek/purge per session, find sessions, session state), transfer DLQ, scheduled & deferred messages, throttling/back-off on bulk ops, emulator support | Built — integration tests written, not yet run against a live namespace |
| 5. Polish | Search/filter, export to JSON, send / edit & resend, self-contained packages per OS, CI + tagged releases | Built — unit tests pass, Windows/Linux packages built locally; dialogs and macOS packaging not yet exercised end to end |

## 7. Future improvements
* **Entra ID authentication** (`DefaultAzureCredential` / `az login`) alongside connection strings.
* **Browse namespaces from a subscription** via Azure Resource Manager (pick instead of paste).
* **Session state editing** (not just view/clear) and resubmitting deferred messages.
* **Server-side batch delete** for purge once `DeleteMessagesAsync` is public in the SDK.
* **Filtered resubmit/purge**: by dead-letter reason, enqueued time window, property match, or body text.
* **Targeted topic resubmit**: stamp a property and create/verify a matching subscription rule so a resubmitted
  message reaches only its original subscription.
* **Message templates** for the send dialog.
* **Import** an exported JSON file to replay messages into another entity or namespace.
* **Server-side search** (beyond the loaded messages), e.g. scanning a whole DLQ for a property value.
* **Entity management**: create/delete queues, topics and subscriptions; edit the properties shown in the
  Properties tab; add/edit/remove subscription rules.
* **Manual theme switch** (light / dark / system) in addition to following the OS.
* **Auto-refresh** counts with a configurable interval; alerts when DLQ count crosses a threshold.
* **Audit log** of destructive operations (local file).
* **Saved layout** (sidebar width, splitter positions).
* **Signed installers**: MSIX/winget (Windows), notarised DMG (macOS), AppImage/Flatpak (Linux); app icon. Signing would also let the in-place updater verify packages by signature, not just checksum.
* **Trimmed builds** to shrink the ~45 MB packages (needs Avalonia/SDK trimming annotations to be verified).
* **CLI companion** reusing `ServiceBusCoordinator.Core` for scripted purge/resubmit.
