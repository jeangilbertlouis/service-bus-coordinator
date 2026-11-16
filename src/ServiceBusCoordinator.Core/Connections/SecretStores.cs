using Microsoft.Identity.Client.Extensions.Msal;

namespace ServiceBusCoordinator.Core.Connections;

/// <summary>Stores a single opaque secret blob.</summary>
public interface ISecretStore
{
    byte[] Read();

    void Write(byte[] data);

    /// <summary>True when data survives an app restart.</summary>
    bool IsPersistent { get; }
}

/// <summary>
/// OS-backed secure storage: DPAPI-encrypted file on Windows, Keychain on macOS, libsecret keyring on Linux.
/// </summary>
public sealed class OsSecretStore : ISecretStore
{
    private const string AppName = "ServiceBusCoordinator";
    private readonly Storage _storage;

    private OsSecretStore(Storage storage) => _storage = storage;

    public bool IsPersistent => true;

    /// <summary>Creates the store and verifies it works. Throws if the OS secret store is unavailable.</summary>
    public static OsSecretStore Create(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        Directory.CreateDirectory(directory);

        var props = new StorageCreationPropertiesBuilder("connections.bin", directory)
            .WithMacKeyChain(AppName, "connections")
            .WithLinuxKeyring(
                "com.servicebuscoordinator.connections",
                MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "Service Bus Coordinator connections",
                new KeyValuePair<string, string>("Version", "1"),
                new KeyValuePair<string, string>("ProductGroup", AppName))
            .Build();

        var storage = Storage.Create(props);
        storage.VerifyPersistence();
        return new OsSecretStore(storage);
    }

    public byte[] Read() => _storage.ReadData() ?? [];

    public void Write(byte[] data) => _storage.WriteData(data);
}

/// <summary>Session-only store, used when OS secure storage is unavailable, and in tests.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private byte[] _data = [];

    public bool IsPersistent => false;

    public byte[] Read() => _data;

    public void Write(byte[] data) => _data = data.ToArray();
}
