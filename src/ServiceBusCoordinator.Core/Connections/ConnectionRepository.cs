using System.Text.Json;

namespace ServiceBusCoordinator.Core.Connections;

/// <summary>A top-level sidebar entry: a folder with its connections, or a connection outside any folder.</summary>
public sealed record ConnectionLayoutItem(ConnectionFolder? Folder, IReadOnlyList<ConnectionProfile> Connections)
{
    public static ConnectionLayoutItem ForFolder(ConnectionFolder folder, IReadOnlyList<ConnectionProfile> connections) => new(folder, connections);

    public static ConnectionLayoutItem ForConnection(ConnectionProfile connection) => new(null, [connection]);

    public bool IsFolder => Folder is not null;
}

/// <summary>
/// Everything the sidebar persists: the connections, the folders, and the order of the top-level entries.
/// The order of connections inside a folder is their order in <see cref="Connections"/>.
/// </summary>
public sealed record ConnectionLayout(
    IReadOnlyList<ConnectionProfile> Connections,
    IReadOnlyList<ConnectionFolder> Folders,
    IReadOnlyList<Guid> RootOrder)
{
    public static ConnectionLayout Empty { get; } = new([], [], []);

    /// <summary>
    /// The top-level entries in display order. Entries missing from <see cref="RootOrder"/> (for example from an older
    /// version) are appended, and a connection whose folder no longer exists is shown at the top level.
    /// </summary>
    public IReadOnlyList<ConnectionLayoutItem> Arrange()
    {
        var folders = new Dictionary<Guid, ConnectionFolder>();
        foreach (var f in Folders)
        {
            folders.TryAdd(f.Id, f);
        }

        bool InFolder(ConnectionProfile c) => c.FolderId is { } id && folders.ContainsKey(id);
        var rootConnections = Connections.Where(c => !InFolder(c)).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());

        var items = new List<ConnectionLayoutItem>();
        var placed = new HashSet<Guid>();

        ConnectionLayoutItem FolderItem(ConnectionFolder f) =>
            ConnectionLayoutItem.ForFolder(f, Connections.Where(c => c.FolderId == f.Id).ToList());

        foreach (var id in RootOrder)
        {
            if (!placed.Add(id))
            {
                continue;
            }

            if (folders.TryGetValue(id, out var folder))
            {
                items.Add(FolderItem(folder));
            }
            else if (rootConnections.TryGetValue(id, out var connection))
            {
                items.Add(ConnectionLayoutItem.ForConnection(connection));
            }
        }

        foreach (var folder in folders.Values.Where(f => placed.Add(f.Id)))
        {
            items.Add(FolderItem(folder));
        }

        foreach (var connection in Connections.Where(c => !InFolder(c) && placed.Add(c.Id)))
        {
            items.Add(ConnectionLayoutItem.ForConnection(connection));
        }

        return items;
    }

    /// <summary>Builds the layout from the sidebar as displayed, setting each connection's <see cref="ConnectionProfile.FolderId"/>.</summary>
    public static ConnectionLayout FromItems(IEnumerable<ConnectionLayoutItem> items)
    {
        var connections = new List<ConnectionProfile>();
        var folders = new List<ConnectionFolder>();
        var order = new List<Guid>();
        foreach (var item in items)
        {
            if (item.Folder is { } folder)
            {
                folders.Add(folder);
                order.Add(folder.Id);
                foreach (var c in item.Connections)
                {
                    c.FolderId = folder.Id;
                    connections.Add(c);
                }
            }
            else
            {
                foreach (var c in item.Connections)
                {
                    c.FolderId = null;
                    connections.Add(c);
                    order.Add(c.Id);
                }
            }
        }

        return new ConnectionLayout(connections, folders, order);
    }
}

/// <summary>Loads and saves all connection profiles (and the sidebar folders) as one blob in an <see cref="ISecretStore"/>.</summary>
public sealed class ConnectionRepository(ISecretStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public bool IsPersistent => store.IsPersistent;

    public IReadOnlyList<ConnectionProfile> Load() => LoadLayout().Connections;

    public ConnectionLayout LoadLayout()
    {
        var data = store.Read();
        if (data.Length == 0)
        {
            return ConnectionLayout.Empty;
        }

        var doc = JsonSerializer.Deserialize<StoredConnections>(data, JsonOptions);
        return doc is null ? ConnectionLayout.Empty : new ConnectionLayout(doc.Connections, doc.Folders, doc.RootOrder);
    }

    /// <summary>Saves connections without folders (all at the top level, in the given order).</summary>
    public void Save(IEnumerable<ConnectionProfile> profiles) =>
        SaveLayout(ConnectionLayout.FromItems(profiles.Select(ConnectionLayoutItem.ForConnection)));

    public void SaveLayout(ConnectionLayout layout)
    {
        var doc = new StoredConnections
        {
            Version = 2,
            Connections = layout.Connections.ToList(),
            Folders = layout.Folders.ToList(),
            RootOrder = layout.RootOrder.ToList(),
        };
        store.Write(JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions));
    }

    private sealed class StoredConnections
    {
        public int Version { get; set; }

        public List<ConnectionProfile> Connections { get; set; } = [];

        /// <summary>Added in version 2; absent in older files, which then load with every connection at the top level.</summary>
        public List<ConnectionFolder> Folders { get; set; } = [];

        public List<Guid> RootOrder { get; set; } = [];
    }
}
