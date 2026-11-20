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
        RootNodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoConnections));
    }

    /// <summary>The sidebar's top level: connection folders and connections outside any folder, in display order.</summary>
    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];

    /// <summary>Every connection, in display order, whether or not it is in a folder.</summary>
    public IEnumerable<NamespaceNodeViewModel> Namespaces => RootNodes.SelectMany(n => n switch
    {
        ConnectionFolderNodeViewModel folder => folder.Connections,
        NamespaceNodeViewModel ns => [ns],
        _ => [],
    });

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveConnectionCommand), nameof(RefreshNamespaceCommand), nameof(AddEntityCommand),
        nameof(RefreshSelectedCommand), nameof(PurgeSelectedCommand), nameof(RenameFolderCommand), nameof(DeleteSelectedEntityCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedNamespace), nameof(CanPurgeSelectedNode), nameof(SelectedFolder), nameof(IsFolderSelected),
        nameof(DeleteSelectedLabel))]
    public partial TreeNodeViewModel? SelectedNode { get; set; }

    public NamespaceNodeViewModel? SelectedNamespace => SelectedNode?.Namespace;

    public ConnectionFolderNodeViewModel? SelectedFolder => SelectedNode as ConnectionFolderNodeViewModel;

    public bool IsFolderSelected => SelectedFolder is not null;

    /// <summary>Whether the selected tree node holds messages that can be purged (a queue or subscription).</summary>
    public bool CanPurgeSelectedNode => SelectedNode is EntityNodeViewModel { Entity.HasMessages: true };

    /// <summary>The load started when an entity is selected, so context-menu actions can wait for it.</summary>
    private Task _entityLoad = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntity))]
    public partial EntityViewModel? CurrentEntity { get; set; }

    public bool HasEntity => CurrentEntity is not null;

    public bool HasNoConnections => RootNodes.Count == 0;

    public string AppVersion { get; } = typeof(MainWindowViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

    [ObservableProperty]
    public partial string? StorageWarning { get; set; }

    // ----------------------------------------------------------------- Zoom

    /// <summary>How much the window content is scaled (1 = 100%).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText), nameof(IsZoomed))]
    public partial double Zoom { get; set; } = 1.0;

    public string ZoomText => $"{Zoom * 100:0}%";

    public bool IsZoomed => Math.Abs(Zoom - 1.0) > 0.001;

    private int _zoomSaveVersion;

    partial void OnZoomChanged(double value)
    {
        var clamped = AppOptions.ClampZoom(value);
        if (clamped != value)
        {
            Zoom = clamped;
            return;
        }

        if (Options.Zoom == value)
        {
            return;
        }

        Options.Zoom = value;
        _ = SaveZoomSoonAsync();
    }

    /// <summary>Saves the zoom once it settles, so scrolling the wheel does not write the file for every step.</summary>
    private async Task SaveZoomSoonAsync()
    {
        var version = ++_zoomSaveVersion;
        await Task.Delay(600);
        if (version != _zoomSaveVersion)
        {
            return;
        }

        try
        {
            _optionsStore?.Save(Options);
        }
        catch (Exception)
        {
            // The zoom still applies until the app closes.
        }
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = AppOptions.StepZoom(Zoom, zoomIn: true);

    [RelayCommand]
    private void ZoomOut() => Zoom = AppOptions.StepZoom(Zoom, zoomIn: false);

    [RelayCommand]
    private void ResetZoom() => Zoom = 1.0;

    private bool HasNamespace => SelectedNamespace is not null;

    private bool CanRemoveSelected => SelectedNamespace is not null || SelectedFolder is not null;

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

        Zoom = Options.Zoom;

        var layout = ConnectionLayout.Empty;
        try
        {
            layout = _repository.LoadLayout();
        }
        catch (Exception ex)
        {
            StorageWarning = "Saved connections could not be read: " + SecretScrubber.Scrub(ex.Message);
        }

        LoadLayout(layout);

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

    /// <summary>Fills the sidebar from saved connections and folders.</summary>
    public void LoadLayout(ConnectionLayout layout)
    {
        RootNodes.Clear();
        foreach (var item in layout.Arrange())
        {
            if (item.Folder is { } folder)
            {
                var folderNode = NewFolderNode(folder);
                foreach (var profile in item.Connections)
                {
                    folderNode.Children.Add(new NamespaceNodeViewModel(profile, Options));
                }

                RootNodes.Add(folderNode);
            }
            else
            {
                RootNodes.Add(new NamespaceNodeViewModel(item.Connections[0], Options));
            }
        }
    }

    private ConnectionFolderNodeViewModel NewFolderNode(ConnectionFolder folder)
    {
        var node = new ConnectionFolderNodeViewModel(folder);
        node.ExpansionChanged += Save;
        return node;
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

        // Like every other connection it stays collapsed and connects only when expanded or via Connect.
        // It goes into the selected folder (or the folder of the selected connection), otherwise at the end.
        var node = new NamespaceNodeViewModel(profile, Options);
        var folder = SelectedFolder ?? FolderOf(SelectedNamespace);
        if (folder is not null)
        {
            folder.Children.Add(node);
            folder.IsExpanded = true;
        }
        else
        {
            RootNodes.Add(node);
        }

        Save();
        SelectedNode = node;
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
        foreach (var ns in Namespaces)
        {
            ns.ApplyOptions();
        }
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

    /// <summary>Removes the selected connection, or the selected folder (keeping its connections).</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveConnectionAsync()
    {
        if (SelectedFolder is { } selectedFolder)
        {
            await DeleteFolderAsync(selectedFolder);
            return;
        }

        if (SelectedNamespace is not { } ns)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Remove connection", $"Remove '{ns.Profile.Name}' and forget its connection string?", "Remove"))
        {
            return;
        }

        SelectedNode = null;
        ContainerOf(ns)?.Remove(ns);
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

    // ----------------------------------------------------------------- Connection folders

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var name = (await _dialogs.PromptAsync("New folder", "Name the folder. Drag connections onto it to file them.", "e.g. Production"))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var node = NewFolderNode(new ConnectionFolder { Name = name });
        RootNodes.Add(node);
        Save();
        SelectedNode = node;
    }

    [RelayCommand(CanExecute = nameof(IsFolderSelected))]
    private async Task RenameFolderAsync()
    {
        if (SelectedFolder is not { } folder)
        {
            return;
        }

        var name = (await _dialogs.PromptAsync("Rename folder", "New name for the folder.", "Folder name", folder.Title))?.Trim();
        if (!string.IsNullOrEmpty(name) && name != folder.Title)
        {
            folder.Rename(name);
            Save();
        }
    }

    private async Task DeleteFolderAsync(ConnectionFolderNodeViewModel folder)
    {
        var count = folder.Children.Count;
        if (count > 0 && !await _dialogs.ConfirmAsync(
                "Delete folder",
                $"Delete the folder '{folder.Title}'? Its {(count == 1 ? "connection" : $"{count} connections")} will move out of the folder, not be removed.",
                "Delete folder"))
        {
            return;
        }

        SelectedNode = null;
        var index = RootNodes.IndexOf(folder);
        RootNodes.RemoveAt(index);
        foreach (var child in folder.Children.ToList())
        {
            folder.Children.Remove(child);
            RootNodes.Insert(index++, child);
        }

        Save();
    }

    private ConnectionFolderNodeViewModel? FolderOf(NamespaceNodeViewModel? ns) =>
        ns is null ? null : RootNodes.OfType<ConnectionFolderNodeViewModel>().FirstOrDefault(f => f.Children.Contains(ns));

    /// <summary>The collection holding a top-level node or a connection: the root, or a folder's children.</summary>
    private ObservableCollection<TreeNodeViewModel>? ContainerOf(TreeNodeViewModel node) =>
        RootNodes.Contains(node) ? RootNodes : node is NamespaceNodeViewModel ns ? FolderOf(ns)?.Children : null;

    // ----------------------------------------------------------------- Drag and drop

    /// <summary>
    /// Works out where a dragged connection or folder would land when dropped over <paramref name="over"/>, with
    /// <paramref name="fraction"/> the pointer's height within that row (0 = top, 1 = bottom). Null when it can't go there.
    /// </summary>
    public DropTarget? ResolveDrop(TreeNodeViewModel dragged, TreeNodeViewModel? over, double fraction)
    {
        if (dragged is not (NamespaceNodeViewModel or ConnectionFolderNodeViewModel) || over is null)
        {
            return null;
        }

        // Over a queue, topic or placeholder: act on the connection it belongs to, below it.
        if (over is not (NamespaceNodeViewModel or ConnectionFolderNodeViewModel))
        {
            over = over.Namespace;
            fraction = 1;
            if (over is null)
            {
                return null;
            }
        }

        // Folders stay at the top level: over a connection inside a folder means next to that folder.
        if (dragged is ConnectionFolderNodeViewModel && over is NamespaceNodeViewModel inner && FolderOf(inner) is { } parent)
        {
            over = parent;
            fraction = fraction < 0.5 && parent.Children.IndexOf(inner) == 0 ? 0 : 1;
        }

        if (ReferenceEquals(over, dragged))
        {
            return null;
        }

        if (over is ConnectionFolderNodeViewModel && dragged is NamespaceNodeViewModel)
        {
            return new DropTarget(over, fraction < 0.25 ? DropPlacement.Before : fraction > 0.75 ? DropPlacement.After : DropPlacement.Into);
        }

        return new DropTarget(over, fraction < 0.5 ? DropPlacement.Before : DropPlacement.After);
    }

    /// <summary>Moves a connection or folder to a drop target and saves the new layout. Returns false if nothing moved.</summary>
    public bool Move(TreeNodeViewModel dragged, DropTarget target)
    {
        var from = ContainerOf(dragged);
        ObservableCollection<TreeNodeViewModel>? to;
        int index;
        if (target.Placement == DropPlacement.Into)
        {
            if (target.Node is not ConnectionFolderNodeViewModel folder || dragged is not NamespaceNodeViewModel)
            {
                return false;
            }

            to = folder.Children;
            index = to.Count;
        }
        else
        {
            to = ContainerOf(target.Node);
            index = to?.IndexOf(target.Node) ?? -1;
            if (target.Placement == DropPlacement.After)
            {
                index++;
            }
        }

        if (from is null || to is null || index < 0)
        {
            return false;
        }

        var oldIndex = from.IndexOf(dragged);
        if (ReferenceEquals(from, to))
        {
            if (oldIndex < index)
            {
                index--;
            }

            if (oldIndex == index)
            {
                return false;
            }

            from.Move(oldIndex, index);
        }
        else
        {
            from.RemoveAt(oldIndex);
            to.Insert(index, dragged);
        }

        if (target.Node is ConnectionFolderNodeViewModel { IsExpanded: false } into && target.Placement == DropPlacement.Into)
        {
            into.IsExpanded = true;
        }

        Save();
        SelectedNode = dragged;
        return true;
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

    /// <summary>"Delete queue…" or "Delete subscription…" for the selected entity.</summary>
    public string DeleteSelectedLabel => SelectedNode is EntityNodeViewModel { IsSubscription: true } ? "Delete subscription…" : "Delete queue…";

    private bool CanDeleteSelectedEntity() => CanPurgeSelectedNode;

    /// <summary>
    /// Deletes the selected queue or subscription from the namespace, with all its messages, after the user types its
    /// name. The tree and totals update without reloading the namespace.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedEntity))]
    private async Task DeleteSelectedEntityAsync()
    {
        if (SelectedNode is not EntityNodeViewModel { Entity.HasMessages: true } node || node.Namespace is not { } ns)
        {
            return;
        }

        var entity = node.Entity;
        var kind = entity.Kind == EntityKind.Subscription ? "subscription" : "queue";
        var where = entity.Kind == EntityKind.Subscription ? $" from topic '{entity.TopicName}'" : "";
        var messages = entity.Counts is { } c && c.Active + c.DeadLetter + c.Scheduled + c.TransferDeadLetter > 0
            ? $" It holds {c.Active:N0} active and {c.DeadLetter:N0} dead-lettered messages, which are deleted with it."
            : "";
        if (!await _dialogs.ConfirmAsync(
                $"Delete {kind}",
                $"Permanently delete the {kind} '{entity.Name}'{where} in {ns.Profile.Name}?{messages}\n\n"
                + $"This cannot be undone. Anything sending to or reading from it will start failing. Type the {kind} name to confirm.",
                $"Delete {kind}",
                typeToConfirm: entity.Name))
        {
            return;
        }

        try
        {
            if (ns.Connection is not { } connection)
            {
                throw new InvalidOperationException("The connection is not open. Expand it and try again.");
            }

            if (CurrentEntity is { } current && ReferenceEquals(SelectedNode, node))
            {
                current.CancelCommand.Execute(null);
            }

            await connection.DeleteEntityAsync(entity);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync($"Delete {kind}", $"The {kind} '{entity.Name}' could not be deleted: " + SecretScrubber.Scrub(ex.Message));
            return;
        }

        SelectedNode = (TreeNodeViewModel?)node.ParentTopic ?? (TreeNodeViewModel?)node.Folder ?? ns;
        ns.RemoveEntity(node);
        Save();
    }

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

    // ----------------------------------------------------------------- Export / import

    /// <summary>The sidebar as layout entries, in display order.</summary>
    private List<ConnectionLayoutItem> LayoutItems() => RootNodes.Select(n => n switch
    {
        ConnectionFolderNodeViewModel f => ConnectionLayoutItem.ForFolder(f.Folder, f.Connections.Select(c => c.Profile).ToList()),
        _ => ConnectionLayoutItem.ForConnection(((NamespaceNodeViewModel)n).Profile),
    }).ToList();

    /// <summary>Saves every connection, with its folder and the sidebar order, to a file (optionally password-protected).</summary>
    [RelayCommand]
    private async Task ExportConnectionsAsync()
    {
        var count = Namespaces.Count();
        if (count == 0)
        {
            await _dialogs.ShowMessageAsync("Export connections", "There are no connections to export yet.");
            return;
        }

        if (await _dialogs.ChooseExportAsync(count) is not { } choice)
        {
            return;
        }

        try
        {
            var data = await Task.Run(() => ConnectionTransfer.Export(LayoutItems(), choice.Password));
            await using var stream = await _dialogs.SaveJsonFileAsync("Export connections", $"service-bus-connections-{DateTime.Now:yyyy-MM-dd}.json");
            if (stream is null)
            {
                return;
            }

            if (stream.CanSeek)
            {
                stream.SetLength(0);
            }

            await stream.WriteAsync(data);
            await _dialogs.ShowMessageAsync("Export connections",
                $"Exported {Plural(count, "connection")}" + (choice.Password is null
                    ? ". The file is not password-protected, so keep it somewhere safe."
                    : ". You will need the password to import them."));
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Export connections", "The connections could not be exported: " + SecretScrubber.Scrub(ex.Message));
        }
    }

    /// <summary>
    /// Adds the connections from an export file. Folders are matched by name, connections already saved (same connection
    /// string) are skipped, and nothing is connected until it is expanded.
    /// </summary>
    [RelayCommand]
    private async Task ImportConnectionsAsync()
    {
        byte[] data;
        try
        {
            await using var stream = await _dialogs.OpenJsonFileAsync("Import connections");
            if (stream is null)
            {
                return;
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            data = buffer.ToArray();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Import connections", "The file could not be read: " + SecretScrubber.Scrub(ex.Message));
            return;
        }

        IReadOnlyList<ConnectionLayoutItem> imported;
        try
        {
            string? password = null;
            string? error = null;
            while (true)
            {
                if (ConnectionTransfer.IsEncrypted(data))
                {
                    password = await _dialogs.PromptPasswordAsync("Import connections", "This file is protected with a password. Enter it to import the connections.", error);
                    if (password is null)
                    {
                        return;
                    }
                }

                try
                {
                    var attempt = password;
                    imported = await Task.Run(() => ConnectionTransfer.Import(data, attempt));
                    break;
                }
                catch (UnauthorizedAccessException ex)
                {
                    error = ex.Message;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            await _dialogs.ShowMessageAsync("Import connections", ex.Message);
            return;
        }

        var summary = Merge(imported);
        Save();
        await _dialogs.ShowMessageAsync("Import connections", DescribeImport(summary));
    }

    /// <summary>Adds imported entries to the sidebar without disturbing the connections already there.</summary>
    private ImportSummary Merge(IReadOnlyList<ConnectionLayoutItem> imported)
    {
        var known = Namespaces.Select(n => n.Profile.ConnectionString).ToList();
        int added = 0, skipped = 0, foldersCreated = 0;

        IEnumerable<ConnectionProfile> Fresh(IEnumerable<ConnectionProfile> connections)
        {
            foreach (var c in connections)
            {
                if (known.Any(k => ConnectionTransfer.SameConnection(k, c.ConnectionString)))
                {
                    skipped++;
                    continue;
                }

                known.Add(c.ConnectionString);
                added++;
                yield return c;
            }
        }

        foreach (var item in imported)
        {
            if (item.Folder is { } folder)
            {
                var fresh = Fresh(item.Connections).ToList();
                var target = RootNodes.OfType<ConnectionFolderNodeViewModel>()
                    .FirstOrDefault(f => string.Equals(f.Title, folder.Name, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    target = NewFolderNode(folder);
                    RootNodes.Add(target);
                    foldersCreated++;
                }

                foreach (var c in fresh)
                {
                    target.Children.Add(new NamespaceNodeViewModel(c, Options));
                }
            }
            else
            {
                foreach (var c in Fresh(item.Connections))
                {
                    RootNodes.Add(new NamespaceNodeViewModel(c, Options));
                }
            }
        }

        return new ImportSummary(added, skipped, foldersCreated);
    }

    private static string DescribeImport(ImportSummary s)
    {
        if (s.Added == 0 && s.Skipped == 0)
        {
            return "The file contains no connections.";
        }

        var text = s.Added == 0
            ? "Nothing new to add."
            : $"Added {Plural(s.Added, "connection")}" + (s.FoldersCreated > 0 ? $" and {Plural(s.FoldersCreated, "folder")}." : ".");
        if (s.Skipped > 0)
        {
            text += $" {Plural(s.Skipped, "connection")} {(s.Skipped == 1 ? "was" : "were")} already saved and skipped.";
        }

        return text + (s.Added > 0 ? " They connect when you expand them." : "");
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    /// <summary>
    /// Collapses every folder, connection, Queues/Topics folder and topic in the sidebar. Connections stay connected;
    /// expanding one again shows it collapsed inside.
    /// </summary>
    [RelayCommand]
    private void CollapseAll()
    {
        _deferSave = true;
        try
        {
            foreach (var node in RootNodes)
            {
                Collapse(node);
            }
        }
        finally
        {
            _deferSave = false;
        }

        Save();

        static void Collapse(TreeNodeViewModel node)
        {
            foreach (var child in node.Children)
            {
                Collapse(child);
            }

            node.IsExpanded = false;
        }
    }

    /// <summary>Set while a batch of changes is made, so the connections are saved once at the end.</summary>
    private bool _deferSave;

    private void Save()
    {
        if (_deferSave)
        {
            return;
        }

        try
        {
            _repository.SaveLayout(ConnectionLayout.FromItems(RootNodes.Select(n => n switch
            {
                ConnectionFolderNodeViewModel f => ConnectionLayoutItem.ForFolder(f.Folder, f.Connections.Select(c => c.Profile).ToList()),
                _ => ConnectionLayoutItem.ForConnection(((NamespaceNodeViewModel)n).Profile),
            })));
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
