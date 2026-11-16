# Service Bus Coordinator

A cross-platform desktop app (Windows, macOS, Linux) for exploring Azure Service Bus namespaces:
browse queues, topics and subscriptions across several namespaces, peek messages, purge, and move
dead-lettered messages back to their main entity.

Built with .NET 10 and Avalonia UI. See [docs/DESIGN.md](docs/DESIGN.md) for the design, decisions, phases and future improvements.

## Run

```bash
dotnet run --project src/ServiceBusCoordinator.App
```

## Package

Self-contained single-file builds; output goes to `artifacts/`:

```bash
powershell -File build/publish.ps1 -Runtime win-x64
```

```bash
bash build/publish.sh linux-x64
```

```bash
bash build/publish.sh osx-arm64
```

CI (`.github/workflows/ci.yml`) builds all of these on every push. Pushing a tag like `v0.5.0` publishes a GitHub release.
The packages are unsigned, so expect a SmartScreen or Gatekeeper prompt on first launch.

## Test

```bash
dotnet test
```

Integration tests are skipped unless `SBC_TEST_CONNECTION` is set to a connection string with Manage rights.

### Local emulator (optional)

`emulator/` contains a Docker Compose setup for the Azure Service Bus emulator, with sample queues
(`orders`, `orders-sessions`) and a topic (`events` → `audit`, `billing`). Copy `emulator/.env.example`
to `emulator/.env`, review and accept the EULAs, and set a local SQL password. Then run:

```bash
docker compose -f emulator/docker-compose.yaml up -d
```

Use this connection string in the app or for `SBC_TEST_CONNECTION`:
`Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`

## Features

- **Multiple namespaces**: add as many connections as you like. Connection strings are kept in OS secure storage
  (DPAPI on Windows, Keychain on macOS, libsecret on Linux).
- **Entity tree** with active / dead-letter counts. Needs a connection string with **Manage** rights; without it,
  add queues or subscriptions by name with **+ Entity**.
- **Peek messages** (non-destructive) on the active queue or the dead-letter queue, with paging, a formatted body
  (JSON / XML / text / hex) and all system and application properties.
- **Purge** the active queue or the dead-letter queue, with confirmation, progress and cancel.
- **Sub-queues**: active, dead-letter and transfer dead-letter.
- **Sessions**: browse session-enabled entities one session at a time, find sessions, and view or clear session state.
- **Scheduled and deferred messages**: shown with their state. You can cancel scheduled messages or delete deferred ones; purge removes both.
- **Throttling**: bulk operations wait and retry when the namespace is busy, instead of failing.
- **Filter** loaded messages by any text, sequence number or `property=value`.
- **Export** ticked, shown, or all messages (up to 10,000) to JSON.
- **Send** new messages or **edit & resend** an existing one, with properties, session ID, TTL and scheduling.
- **Resubmit** selected or all dead-lettered messages to the original queue or topic, or to another entity.
  Uses a transaction when the service allows it. Warns when resubmitting to a topic, because the message will go to every matching subscription.

## Layout

```
src/ServiceBusCoordinator.Core   Azure SDK logic (no UI)
src/ServiceBusCoordinator.App    Avalonia desktop app (MVVM)
tests/ServiceBusCoordinator.Core.Tests
docs/DESIGN.md                   Design, decisions, roadmap
```
