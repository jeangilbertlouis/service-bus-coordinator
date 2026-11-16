namespace ServiceBusCoordinator.Core.Connections;

/// <summary>A saved namespace connection. Persisted only through an <see cref="ISecretStore"/>.</summary>
public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string ConnectionString { get; set; } = "";

    /// <summary>Entity paths added by hand (used when the connection lacks Manage rights).</summary>
    public List<string> ManualEntities { get; set; } = [];
}
