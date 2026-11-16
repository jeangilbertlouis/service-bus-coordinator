# Service Bus Coordinator

A cross-platform desktop app (Windows, macOS, Linux) for exploring Azure Service Bus namespaces:
browse queues, topics and subscriptions across several namespaces, peek messages, purge, and move
dead-lettered messages back to their main entity.

Built with .NET 10 and Avalonia UI. See [docs/DESIGN.md](docs/DESIGN.md) for the design, decisions, phases and future improvements.

## Run

```bash
dotnet run --project src/ServiceBusCoordinator.App
```

## Test

```bash
dotnet test
```

## Features (v1)

- **Multiple namespaces**: add as many connections as you like. Connection strings are kept in OS secure storage
  (DPAPI on Windows, Keychain on macOS, libsecret on Linux).
- **Entity tree** with active / dead-letter counts. Needs a connection string with **Manage** rights; without it,
  add queues or subscriptions by name with **+ Entity**.
- **Peek messages** (non-destructive) on the active queue or the dead-letter queue, with paging, a formatted body
  (JSON / XML / text / hex) and all system and application properties.
- **Purge** the active queue or the dead-letter queue, with confirmation, progress and cancel.
- **Resubmit** selected or all dead-lettered messages to the original queue or topic, or to another entity.
  Uses a transaction when the service allows it. Warns when resubmitting to a topic, because the message will go to every matching subscription.

## Layout

```
src/ServiceBusCoordinator.Core   Azure SDK logic (no UI)
src/ServiceBusCoordinator.App    Avalonia desktop app (MVVM)
tests/ServiceBusCoordinator.Core.Tests
docs/DESIGN.md                   Design, decisions, roadmap
```
