using System.Text.Json;

namespace ServiceBusCoordinator.Core.Connections;

/// <summary>Loads and saves all connection profiles as one blob in an <see cref="ISecretStore"/>.</summary>
public sealed class ConnectionRepository(ISecretStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public bool IsPersistent => store.IsPersistent;

    public IReadOnlyList<ConnectionProfile> Load()
    {
        var data = store.Read();
        if (data.Length == 0)
        {
            return [];
        }

        var doc = JsonSerializer.Deserialize<StoredConnections>(data, JsonOptions);
        return doc?.Connections ?? [];
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        var doc = new StoredConnections { Version = 1, Connections = profiles.ToList() };
        store.Write(JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions));
    }

    private sealed class StoredConnections
    {
        public int Version { get; set; }

        public List<ConnectionProfile> Connections { get; set; } = [];
    }
}
