# Service Bus Coordinator

A free desktop app for Windows, macOS and Linux to look inside your Azure Service Bus namespaces: browse queues, topics
and subscriptions, read messages, clear out queues, and send dead-lettered messages back to where they came from.

## What you can do

- **Connect to several namespaces at once** by pasting a connection string. Connection strings are kept in your
  operating system's secure storage, never in a plain file.
- **Browse queues, topics and subscriptions** with live message counts and a clear warning when dead-letter messages
  pile up.
- **Read messages** without removing them, with a formatted body (JSON, XML, text) and all their properties.
- **Fix dead-lettered messages**: resubmit some or all of them to the original queue or topic, or to another one.
- **Purge** a queue or its dead-letter queue, with confirmation, progress and cancel, from the toolbar or by
  right-clicking it in the tree (which also offers **Refresh**).
- **Send** new messages, or edit and resend an existing one.
- **Filter and export** messages to JSON.
- **See entity settings**: lock duration, time-to-live, forwarding, subscription rules and more.
- Works with **sessions**, **scheduled and deferred messages**, and the **local Service Bus emulator**.
- Light and dark themes that follow your OS.
- **Updates itself**: when a new version is released, click **Update and restart**. Set the environment variable
  `SBC_DISABLE_UPDATE_CHECK=1` to stop the check on startup.

## Install

1. Open the [latest release](https://github.com/jeangilbertlouis/service-bus-coordinator/releases/latest).
2. Download the file for your system:

   | System | File |
   |---|---|
   | Windows | `…-win-x64.zip` |
   | macOS (Apple silicon) | `…-osx-arm64.zip` |
   | macOS (Intel) | `…-osx-x64.zip` |
   | Linux | `…-linux-x64.tar.gz` |

3. Unpack it and start **ServiceBusCoordinator** (on macOS, **Service Bus Coordinator.app**). No .NET install is needed.

The app is not code-signed yet, so the first launch shows a warning:

- **Windows:** on the SmartScreen prompt, choose **More info → Run anyway**.
- **macOS:** right-click the app, choose **Open**, then confirm.
- **Linux:** you may need to make the file executable (`chmod +x ServiceBusCoordinator`). Secure storage needs
  `libsecret`, which most desktop distributions already include.

## Get started

1. Click **Add connection** and paste a Service Bus connection string. You can find it in the Azure portal under your
   namespace → **Shared access policies**. The name fills in automatically.
2. Pick a queue or subscription in the tree to see its messages. Switch to **Dead-letter** to see failed messages.
3. Tick messages and choose **Resubmit**, or use **Purge** to clear the list. Both ask before changing anything.

### Which connection string should I use?

| Rights | What works |
|---|---|
| **Manage** | Everything: the full tree, counts, entity settings |
| **Listen** / **Send** only | Messages and operations, but you add queues or subscriptions by name with the **add entity** button next to **Add connection** |

Resubmit and purge change real data. Try them on a test namespace or the
[local emulator](docs/DEVELOPMENT.md#local-emulator-optional) first.

## For developers

Service Bus Coordinator is built with .NET 10 and [Avalonia UI](https://avaloniaui.net/). To run it from source:

```bash
dotnet run --project src/ServiceBusCoordinator.App
```

- [Developer guide](docs/DEVELOPMENT.md): building, testing, packaging, the emulator, the code layout, and how to add
  features.
- [Design document](docs/DESIGN.md): architecture, decisions, and the roadmap of future improvements.

Contributions are welcome. See [Contributing](docs/DEVELOPMENT.md#contributing).

## License

[MIT](LICENSE). Not affiliated with or endorsed by Microsoft.
