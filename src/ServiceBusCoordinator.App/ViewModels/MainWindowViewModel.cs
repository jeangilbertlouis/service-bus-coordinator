using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceBusCoordinator.App.Services;
using ServiceBusCoordinator.Core;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Models;
using ServiceBusCoordinator.Core.Settings;
using ServiceBusCoordinator.Core.Updates;

namespace ServiceBusCoordinator.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private ConnectionRepository _repository = new(new InMemorySecretStore());
    private AppOptionsStore? _optionsStore;

    /// <summary>The user's options. One shared instance, so connections see changes straight away.</summary>
    public AppOptions Options { get; } = new();

    public MainWindowViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Namespaces.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoConnections));
    }

    public ObservableCollection<NamespaceNodeViewModel> Namespaces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveConnectionCommand), nameof(RefreshNamespaceCommand), nameof(AddEntityCommand),
        nameof(RefreshSelectedCommand), nameof(PurgeSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedNamespace), nameof(CanPurgeSelectedNode))]
    public partial TreeNodeViewModel? SelectedNode { get; set; }

    public NamespaceNodeViewModel? SelectedNamespace => SelectedNode?.Namespace;

    /// <summary>Whether the selected tree node holds messages that can be purged (a queue or subscription).</summary>
    public bool CanPurgeSelectedNode => SelectedNode is EntityNodeViewModel { Entity.HasMessages: true };

    /// <summary>The load started when an entity is selected, so context-menu actions can wait for it.</summary>
    private Task _entityLoad = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntity))]
    public partial EntityViewModel? CurrentEntity { get; set; }

    public bool HasEntity => CurrentEntity is not null;

    public bool HasNoConnections => Namespaces.Count == 0;

    public string AppVersion { get; } = typeof(MainWindowViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

    [ObservableProperty]
    public partial string? StorageWarning { get; set; }

    private bool HasNamespace => SelectedNamespace is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _repository = new ConnectionRepository(OsSecretStore.Create());
        }
        catch (Exception ex)
        {
            StorageWarning = "OS secure storage is unavailable, so connections will not be saved after you close the app. "
                + SecretScrubber.Scrub(ex.Message);
        }

        try
        {
            _optionsStore = AppOptionsStore.CreateDefault();
            Options.CopyFrom(_optionsStore.Load());
        }
        catch (Exception)
        {
            // No writable settings folder: run with the defaults.
        }

        IReadOnlyList<ConnectionProfile> profiles = [];
        try
        {
            profiles = _repository.Load();
        }
        catch (Exception ex)
        {
            StorageWarning = "Saved connections could not be read: " + SecretScrubber.Scrub(ex.Message);
        }

        foreach (var profile in profiles)
        {
            Namespaces.Add(new NamespaceNodeViewModel(profile, Options));
        }

        // By default connections stay collapsed and unconnected until the user expands them, so many saved
        // connections cost nothing at startup. The Options dialog can turn connecting at startup back on.
        if (Options.ConnectOnStartup)
        {
            foreach (var ns in Namespaces)
            {
                ns.IsExpanded = true; // Expanding connects.
            }
        }

        if (UpdateService.StartupCheckEnabled && Options.CheckForUpdatesOnStartup)
        {
            await CheckForUpdatesCoreAsync(userAsked: false);
        }
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is EntityNodeViewModel entityNode)
        {
            CurrentEntity = new EntityViewModel(entityNode, _dialogs);
            _entityLoad = CurrentEntity.RefreshAsync();
        }
        else
        {
            CurrentEntity = null;
        }
    }

    [RelayCommand]
    private async Task AddConnectionAsync()
    {
        var profile = await _dialogs.AddConnectionAsync();
        if (profile is null)
        {
            return;
        }

        var node = new NamespaceNodeViewModel(profile, Options);
        Namespaces.Add(node);
        Save();
        SelectedNode = node;
        node.IsExpanded = true; // A connection the user just added is connected straight away.
        await node.LoadAsync();
    }

    /// <summary>Opens the Options dialog and saves the result. Tree options apply to connections connected afterwards.</summary>
    [RelayCommand]
    private async Task EditOptionsAsync()
    {
        var edited = await _dialogs.EditOptionsAsync(Options.Clone());
        if (edited is null)
        {
            return;
        }

        Options.CopyFrom(edited);
        try
        {
            _optionsStore?.Save(Options);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Options",
                "Your options apply until you close the app, but could not be saved: " + SecretScrubber.Scrub(ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(HasNamespace))]
    private async Task RemoveConnectionAsync()
    {
        if (SelectedNamespace is not { } ns)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Remove connection", $"Remove '{ns.Profile.Name}' and forget its connection string?", "Remove"))
        {
            return;
        }

        SelectedNode = null;
        Namespaces.Remove(ns);
        Save();
        await ns.DisposeAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNamespace))]
    private async Task RefreshNamespaceAsync()
    {
        if (SelectedNamespace is { } ns)
        {
            SelectedNode = ns;
            ns.IsExpanded = true;
            await ns.LoadAsync();
        }
    }

    // ----------------------------------------------------------------- Tree context menu

    /// <summary>Refreshes the right-clicked node: an entity reloads its messages and counts, anything else reloads the namespace.</summary>
    [RelayCommand(CanExecute = nameof(HasNamespace))]
    private async Task RefreshSelectedAsync()
    {
        if (SelectedNode is EntityNodeViewModel && CurrentEntity is { } entity)
        {
            await WaitForEntityLoadAsync();
            if (!entity.IsBusy)
            {
                await entity.RefreshAsync();
            }
        }
        else
        {
            await RefreshNamespaceAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPurgeSelected))]
    private async Task PurgeSelectedAsync(MessageSource source)
    {
        if (CurrentEntity is not { } entity || !CanPurgeSelectedNode)
        {
            return;
        }

        await WaitForEntityLoadAsync();
        await entity.PurgeSourceAsync(source);
    }

    private bool CanPurgeSelected(MessageSource source) => CanPurgeSelectedNode;

    private async Task WaitForEntityLoadAsync()
    {
        try
        {
            await _entityLoad;
        }
        catch (Exception)
        {
            // The entity view shows its own load errors.
        }
    }

    [RelayCommand(CanExecute = nameof(HasNamespace))]
    private async Task AddEntityAsync()
    {
        if (SelectedNamespace is not { } ns)
        {
            return;
        }

        var path = await _dialogs.PromptAsync(
            "Add entity by name",
            "Enter a queue name, or a subscription as 'topic/subscriptions/name' (or 'topic/name').",
            "orders  or  events/subscriptions/audit");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var entity = EntityInfo.ParseManual(path);
            if (!ns.Profile.ManualEntities.Contains(entity.Path, StringComparer.OrdinalIgnoreCase))
            {
                ns.Profile.ManualEntities.Add(entity.Path);
                Save();
            }

            ns.IsExpanded = true;
            await ns.LoadAsync();
            if (ns.LimitedAccessReason is not null || ns.Error is not null)
            {
                return;
            }

            await _dialogs.ShowMessageAsync(
                "Entity saved",
                "This connection can list entities, so manually added entries are only used if access becomes limited.");
        }
        catch (FormatException ex)
        {
            await _dialogs.ShowMessageAsync("Invalid entity path", ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            _repository.Save(Namespaces.Select(n => n.Profile));
        }
        catch (Exception ex)
        {
            StorageWarning = "Connections could not be saved: " + SecretScrubber.Scrub(ex.Message);
        }
    }

    // ----------------------------------------------------------------- Updates

    /// <summary>Raised when the app should close, after an update has been installed and the new version started.</summary>
    public event Action? ShutdownRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateTitle), nameof(CanInstallUpdate), nameof(CanOnlyDownloadUpdate))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial AvailableUpdate? Update { get; set; }

    public bool HasUpdate => Update is not null;

    public string UpdateTitle => Update is { } u ? $"Version {u.Version} is available" : "";

    /// <summary>The app can download and install this update itself.</summary>
    public bool CanInstallUpdate => Update is { CanInstallInPlace: true } && UpdateService.Install is not null;

    /// <summary>The update must be downloaded by hand (running from source, or the release lacks this platform's files).</summary>
    public bool CanOnlyDownloadUpdate => HasUpdate && !CanInstallUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand), nameof(CheckForUpdatesCommand))]
    public partial bool IsUpdating { get; set; }

    [ObservableProperty]
    public partial double UpdateProgress { get; set; }

    [ObservableProperty]
    public partial string? UpdateError { get; set; }

    private bool NotUpdating => !IsUpdating;

    private bool CanStartInstall => !IsUpdating && CanInstallUpdate;

    [RelayCommand(CanExecute = nameof(NotUpdating))]
    private Task CheckForUpdatesAsync() => CheckForUpdatesCoreAsync(userAsked: true);

    private async Task CheckForUpdatesCoreAsync(bool userAsked)
    {
        try
        {
            Update = await UpdateService.CheckAsync();
            UpdateError = null;
            if (userAsked && Update is null)
            {
                await _dialogs.ShowMessageAsync("Check for updates", $"You have the latest version ({AppVersion}).");
            }
        }
        catch (Exception ex)
        {
            // A failed background check stays quiet; only report it when the user asked.
            if (userAsked)
            {
                await _dialogs.ShowMessageAsync("Check for updates", "Could not check for updates: " + SecretScrubber.Scrub(ex.Message));
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartInstall))]
    private async Task InstallUpdateAsync()
    {
        if (Update is not { Package: { } package } update)
        {
            return;
        }

        var size = package.Size > 0 ? $" (about {package.Size / (1024.0 * 1024.0):N0} MB)" : "";
        if (!await _dialogs.ConfirmAsync(
                "Update and restart",
                $"Download version {update.Version}{size}, replace this copy ({AppVersion}) and restart?\n\n"
                + "Your connections are kept. Anything in progress, such as a purge or resubmit, is stopped.",
                "Update and restart"))
        {
            return;
        }

        IsUpdating = true;
        UpdateError = null;
        UpdateProgress = 0;
        try
        {
            await UpdateService.InstallAsync(update, new Progress<double>(p => UpdateProgress = p * 100), CancellationToken.None);
        }
        catch (Exception ex)
        {
            UpdateError = "Update failed: " + SecretScrubber.Scrub(ex.Message);
            IsUpdating = false;
            return;
        }

        try
        {
            UpdateService.Relaunch();
        }
        catch (Exception ex)
        {
            IsUpdating = false;
            await _dialogs.ShowMessageAsync(
                "Update installed",
                $"Version {update.Version} is installed, but it could not be started automatically ({SecretScrubber.Scrub(ex.Message)}). Close and reopen the app to use it.");
            return;
        }

        ShutdownRequested?.Invoke();
    }

    [RelayCommand]
    private async Task OpenReleasePageAsync()
    {
        if (Update is { } u)
        {
            await _dialogs.OpenUrlAsync(u.ReleasePage);
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        Update = null;
        UpdateError = null;
    }

    public void Dispose()
    {
        // Best-effort: close AMQP links without blocking the UI thread while the app shuts down.
        foreach (var ns in Namespaces)
        {
            _ = ns.DisposeAsync().AsTask();
        }
    }
}
