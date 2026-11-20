using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceBusCoordinator.Core.Connections;

/// <summary>What an import added and what it left out because it was already there.</summary>
public sealed record ImportSummary(int Added, int Skipped, int FoldersCreated);

/// <summary>
/// Reads and writes the connection export file: every connection with its connection string, the folders and the
/// sidebar order. Because connection strings carry access keys, the file can be encrypted with a password
/// (PBKDF2-SHA256 key, AES-256-GCM). Without a password it is plain JSON.
/// </summary>
public static class ConnectionTransfer
{
    public const string FormatName = "service-bus-coordinator-connections";
    public const int Iterations = 600_000;

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes the sidebar's entries, in display order, encrypted when a password is given.</summary>
    public static byte[] Export(IEnumerable<ConnectionLayoutItem> items, string? password = null)
    {
        var payload = new Payload
        {
            Entries = items.Select(item => new Entry
            {
                Folder = item.Folder?.Name,
                Connections = item.Connections.Select(c => new ExportedConnection
                {
                    Name = c.Name,
                    ConnectionString = c.ConnectionString,
                    ManualEntities = c.ManualEntities.Count > 0 ? [.. c.ManualEntities] : null,
                }).ToList(),
            }).ToList(),
        };

        var file = new ExportFile { Format = FormatName, Version = 1, ExportedAt = DateTimeOffset.UtcNow };
        if (string.IsNullOrEmpty(password))
        {
            file.Entries = payload.Entries;
        }
        else
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(DeriveKey(password, salt, Iterations), TagSize))
            {
                aes.Encrypt(nonce, plain, cipher, tag, Header(salt, Iterations));
            }

            CryptographicOperations.ZeroMemory(plain);
            file.Encryption = new Encryption
            {
                Kdf = "PBKDF2-SHA256",
                Iterations = Iterations,
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Data = Convert.ToBase64String(cipher),
            };
        }

        return JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
    }

    /// <summary>Whether the file needs a password to read. Throws <see cref="InvalidDataException"/> when it is not an export file.</summary>
    public static bool IsEncrypted(byte[] file) => Parse(file).Encryption is not null;

    /// <summary>
    /// Reads an export file into sidebar entries with fresh ids. Throws <see cref="InvalidDataException"/> when the file
    /// is not an export, and <see cref="UnauthorizedAccessException"/> when the password is missing or wrong.
    /// </summary>
    public static IReadOnlyList<ConnectionLayoutItem> Import(byte[] file, string? password = null)
    {
        var doc = Parse(file);
        var entries = doc.Entries;
        if (doc.Encryption is { } enc)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new UnauthorizedAccessException("This file is protected with a password.");
            }

            entries = Decrypt(enc, password).Entries;
        }

        var items = new List<ConnectionLayoutItem>();
        foreach (var entry in entries ?? [])
        {
            var connections = (entry.Connections ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.ConnectionString))
                .Select(c => new ConnectionProfile
                {
                    Name = string.IsNullOrWhiteSpace(c.Name) ? NameFrom(c.ConnectionString!) : c.Name.Trim(),
                    ConnectionString = c.ConnectionString!.Trim(),
                    ManualEntities = c.ManualEntities?.Where(e => !string.IsNullOrWhiteSpace(e)).ToList() ?? [],
                })
                .ToList();

            if (!string.IsNullOrWhiteSpace(entry.Folder))
            {
                items.Add(ConnectionLayoutItem.ForFolder(new ConnectionFolder { Name = entry.Folder.Trim() }, connections));
            }
            else
            {
                items.AddRange(connections.Select(ConnectionLayoutItem.ForConnection));
            }
        }

        return items;
    }

    /// <summary>Whether two connection strings point at the same namespace with the same credentials.</summary>
    public static bool SameConnection(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string connectionString) =>
        string.Join(';', connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2) is [var key, var value] ? $"{key.ToLowerInvariant()}={value.TrimEnd('/')}" : part)
            .Order(StringComparer.Ordinal));

    private static string NameFrom(string connectionString) =>
        ConnectionStringInfo.TryParse(connectionString, out var info, out _) ? info!.NamespaceName : "Imported connection";

    private static ExportFile Parse(byte[] file)
    {
        ExportFile? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ExportFile>(file, JsonOptions);
        }
        catch (JsonException)
        {
            doc = null;
        }

        if (doc is null || doc.Format != FormatName)
        {
            throw new InvalidDataException("This is not a Service Bus Coordinator connections file.");
        }

        if (doc.Version > 1)
        {
            throw new InvalidDataException($"This file was exported by a newer version (format {doc.Version}). Update the app to import it.");
        }

        return doc;
    }

    private static Payload Decrypt(Encryption enc, string password)
    {
        try
        {
            if (enc.Kdf != "PBKDF2-SHA256" || enc.Iterations is < 10_000 or > 10_000_000)
            {
                throw new InvalidDataException("The file's encryption settings are not supported.");
            }

            var salt = Convert.FromBase64String(enc.Salt ?? "");
            var nonce = Convert.FromBase64String(enc.Nonce ?? "");
            var tag = Convert.FromBase64String(enc.Tag ?? "");
            var cipher = Convert.FromBase64String(enc.Data ?? "");
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(DeriveKey(password, salt, enc.Iterations), TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain, Header(salt, enc.Iterations));
            }

            try
            {
                return JsonSerializer.Deserialize<Payload>(plain, JsonOptions) ?? new Payload();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new UnauthorizedAccessException("The password is wrong, or the file has been changed.");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException or JsonException)
        {
            throw new InvalidDataException("The file is damaged and cannot be read.");
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Binds the ciphertext to the format and key settings, so they cannot be swapped.</summary>
    private static byte[] Header(byte[] salt, int iterations) =>
        Encoding.UTF8.GetBytes($"{FormatName}/1/PBKDF2-SHA256/{iterations}/{Convert.ToBase64String(salt)}");

    private sealed class ExportFile
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public DateTimeOffset? ExportedAt { get; set; }

        /// <summary>Present when the file is not password-protected.</summary>
        public List<Entry>? Entries { get; set; }

        /// <summary>Present when it is; the entries are inside, encrypted.</summary>
        public Encryption? Encryption { get; set; }
    }

    private sealed class Payload
    {
        public List<Entry> Entries { get; set; } = [];
    }

    private sealed class Entry
    {
        /// <summary>The folder name, or null for connections at the top level.</summary>
        public string? Folder { get; set; }

        public List<ExportedConnection>? Connections { get; set; }
    }

    private sealed class ExportedConnection
    {
        public string? Name { get; set; }

        public string? ConnectionString { get; set; }

        public List<string>? ManualEntities { get; set; }
    }

    private sealed class Encryption
    {
        public string? Kdf { get; set; }

        public int Iterations { get; set; }

        public string? Salt { get; set; }

        public string? Nonce { get; set; }

        public string? Tag { get; set; }

        public string? Data { get; set; }
    }
}
