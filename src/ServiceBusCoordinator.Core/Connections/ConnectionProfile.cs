namespace ServiceBusCoordinator.Core.Connections;

/// <summary>A saved namespace connection. Persisted only through an <see cref="ISecretStore"/>.</summary>
public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string ConnectionString { get; set; } = "";

    /// <summary>Entity paths added by hand (used when the connection lacks Manage rights).</summary>
    public List<string> ManualEntities { get; set; } = [];

    /// <summary>The folder this connection is filed under in the sidebar, or null for the top level.</summary>
    public Guid? FolderId { get; set; }
}

/// <summary>A sidebar folder that groups connections. Folders are one level deep.</summary>
public sealed class ConnectionFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public bool IsExpanded { get; set; } = true;
}
