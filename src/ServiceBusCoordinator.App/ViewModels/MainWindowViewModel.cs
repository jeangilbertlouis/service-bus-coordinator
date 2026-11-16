using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceBusCoordinator.App.Services;
using ServiceBusCoordinator.Core;
using ServiceBusCoordinator.Core.Connections;
using ServiceBusCoordinator.Core.Models;

namespace ServiceBusCoordinator.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private ConnectionRepository _repository = new(new InMemorySecretStore());

    public MainWindowViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Namespaces.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoConnections));
    }

    public ObservableCollection<NamespaceNodeViewModel> Namespaces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveConnectionCommand), nameof(RefreshNamespaceCommand), nameof(AddEntityCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedNamespace))]
    public partial TreeNodeViewModel? SelectedNode { get; set; }

    public NamespaceNodeViewModel? SelectedNamespace => SelectedNode?.Namespace;

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
            Namespaces.Add(new NamespaceNodeViewModel(profile));
        }

        await Task.WhenAll(Namespaces.Select(n => n.LoadAsync()));
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is EntityNodeViewModel entityNode)
        {
            CurrentEntity = new EntityViewModel(entityNode, _dialogs);
            _ = CurrentEntity.RefreshAsync();
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

        var node = new NamespaceNodeViewModel(profile);
        Namespaces.Add(node);
        Save();
        SelectedNode = node;
        await node.LoadAsync();
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
            await ns.LoadAsync();
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

    public void Dispose()
    {
        // Best-effort: close AMQP links without blocking the UI thread while the app shuts down.
        foreach (var ns in Namespaces)
        {
            _ = ns.DisposeAsync().AsTask();
        }
    }
}
