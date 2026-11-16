# Service Bus Coordinator — Design & Plan

A cross-platform desktop app (Windows, macOS, Linux) for exploring and operating on
Azure Service Bus namespaces.

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

### 3.2 Entity discovery
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
* Confirmation dialog requiring the entity name to be typed for large purges.
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
| 5. Polish | Search/filter, export, send test message, installers per OS | Planned |

## 7. Future improvements
* **Entra ID authentication** (`DefaultAzureCredential` / `az login`) alongside connection strings.
* **Browse namespaces from a subscription** via Azure Resource Manager (pick instead of paste).
* **Session state editing** (not just view/clear) and resubmitting deferred messages.
* **Server-side batch delete** for purge once `DeleteMessagesAsync` is public in the SDK.
* **Filtered resubmit/purge**: by dead-letter reason, enqueued time window, property match, or body text.
* **Targeted topic resubmit**: stamp a property and create/verify a matching subscription rule so a resubmitted
  message reaches only its original subscription.
* **Send / edit & resend** messages (with templates).
* **Export / import** messages to JSON files for backup or replay between namespaces.
* **Search** across message bodies and properties in the loaded page.
* **Entity management**: create/delete/edit queues, topics, subscriptions and rules.
* **Auto-refresh** counts with a configurable interval; alerts when DLQ count crosses a threshold.
* **Audit log** of destructive operations (local file).
* **Dark/light theme toggle** and saved layout.
* **Packaging**: MSIX/winget (Windows), notarised `.app`/DMG (macOS), AppImage/Flatpak (Linux); auto-update.
* **CLI companion** reusing `ServiceBusCoordinator.Core` for scripted purge/resubmit.
