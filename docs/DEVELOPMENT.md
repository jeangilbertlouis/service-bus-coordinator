# Developer guide

How to build, test, package and extend Service Bus Coordinator. For the design, decisions and roadmap,
see [DESIGN.md](DESIGN.md).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Optional: Docker, to run the local Service Bus emulator
- Linux only: `libsecret` (for example `libsecret-1-0` on Debian/Ubuntu), used to store connection strings

## Build and run

```bash
dotnet run --project src/ServiceBusCoordinator.App
```

Close any running copy of the app before rebuilding, because it locks the files in `bin/`.

## Test

```bash
dotnet test
```

Integration tests are skipped unless `SBC_TEST_CONNECTION` is set to a connection string with Manage rights.
Each integration test creates and deletes its own queue, so use the emulator or a disposable namespace, never production.

### Local emulator (optional)

`emulator/` contains a Docker Compose setup for the Azure Service Bus emulator, with sample queues
(`orders`, `orders-sessions`) and a topic (`events` → `audit`, `billing`). Copy `emulator/.env.example`
to `emulator/.env`, review and accept the EULAs, and set a local SQL password. Then run:

```bash
docker compose -f emulator/docker-compose.yaml up -d
```

Use this connection string in the app or for `SBC_TEST_CONNECTION`:
`Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`

The app talks to the emulator's management endpoint on port 5300 automatically when the connection string contains
`UseDevelopmentEmulator=true`.

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

The macOS build is wrapped in a `.app` bundle using `build/macos/Info.plist`.
The packages are unsigned, so expect a SmartScreen or Gatekeeper prompt on first launch.

## CI and releases

CI (`.github/workflows/ci.yml`) runs the tests and builds packages for `win-x64`, `linux-x64`, `osx-arm64` and
`osx-x64` on every push. Packages are attached to the workflow run as artifacts.

To publish a release:

1. Bump `<Version>` in `src/ServiceBusCoordinator.App/ServiceBusCoordinator.App.csproj`.
2. Commit, then push a matching tag, for example `v0.6.0`.
3. CI creates a GitHub release with the packages and a `SHA256SUMS.txt` attached.

Installed copies find the new release on their next start and offer **Update and restart**. The updater needs the
package names produced by `build/publish.*` (`ServiceBusCoordinator-<version>-<rid>.zip|.tar.gz`) and the checksum
file, so keep both if you change packaging. The tag must be a plain `vX.Y.Z`; tags with a pre-release suffix and
releases marked as pre-release are not offered.

## Project layout

```
src/ServiceBusCoordinator.Core          Azure SDK logic, no UI
  Connections/   connection profiles, connection string parsing, OS secret store
  Explorer/      entity discovery, runtime counts, entity properties
  Messages/      peek, purge, resubmit, send, filter, export, throttling
  Models/        plain records shared with the UI
src/ServiceBusCoordinator.App           Avalonia desktop app (MVVM)
  ViewModels/    CommunityToolkit.Mvvm view models
  Views/         XAML views
  Services/      dialogs (IDialogService)
  App.axaml      themes, colors and shared styles
  Icons.cs       vector icons
tests/ServiceBusCoordinator.Core.Tests  xUnit tests
emulator/                               local Service Bus emulator setup
build/                                  packaging scripts
docs/                                   design and developer docs
```

## Extending the app

### Principles

- **Keep Azure logic in Core.** Anything that talks to Service Bus belongs in `ServiceBusCoordinator.Core` and must
  not reference Avalonia. That keeps it testable and reusable, for example by a future CLI.
- **The App is a thin MVVM layer.** View models call Core and expose observable properties and commands. Views bind to
  them with compiled bindings (`x:DataType`).
- **Long operations** take a `CancellationToken` and report progress with `IProgress<OperationProgress>`. Bulk
  operations go through `Throttling.RunAsync` so they back off when the namespace is busy.
- **Never leak secrets.** Don't log connection strings, pass error text through `SecretScrubber`, and show access
  policy names only, never keys.
- **Destructive operations ask first** through `IDialogService.ConfirmAsync`.

### Where to put a change

| You want to… | Start here |
|---|---|
| Add a message operation (for example filtered purge) | `Core/Messages/MessageOperations.cs`, then a `[RelayCommand]` in `App/ViewModels/EntityViewModel.cs` and a toolbar button in `App/Views/MainWindow.axaml` |
| Show more entity properties | `Core/Explorer/EntityDetails.cs` (add items to a `DetailGroup`); the Properties tab renders them automatically |
| Change how entities are discovered | `Core/Explorer/NamespaceConnection.cs` |
| Change the tree | `App/ViewModels/TreeNodes.cs` |
| Add a dialog | `App/Services/IDialogService.cs` and `DialogService.cs` |
| Change colors or styles | `App/App.axaml` (light and dark theme dictionaries) |
| Add an icon | `App/Icons.cs` (16×16 path geometry) |
| Support another way to sign in | `Core/Connections/`; see "Entra ID authentication" in [DESIGN.md](DESIGN.md#7-future-improvements) |

### Example: a new message operation

1. Add an async method to `MessageOperations` that takes the entity, the sub-queue, a `CancellationToken` and an
   `IProgress<OperationProgress>`.
2. Cover its pure logic with unit tests in `tests/ServiceBusCoordinator.Core.Tests`, and its Service Bus behavior with
   an integration test in `IntegrationTests.cs`.
3. In `EntityViewModel`, add a `[RelayCommand]` that confirms with the user if the operation is destructive, then runs
   it through the existing `RunAsync` helper so progress, cancel and errors are handled for you.
4. Bind a button to the command in `MainWindow.axaml`.
5. Document the behavior in [DESIGN.md](DESIGN.md) and, if it is user-facing, in the README feature list.

### Ideas

The roadmap in [DESIGN.md § Future improvements](DESIGN.md#7-future-improvements) lists features that would make good
contributions, such as Entra ID sign-in, filtered resubmit, entity management and signed installers.

## Contributing

1. Fork the repository and create a branch.
2. Make your change, with tests where possible.
3. Run `dotnet test` and check the app still starts.
4. Open a pull request describing what changed and how you tested it.
