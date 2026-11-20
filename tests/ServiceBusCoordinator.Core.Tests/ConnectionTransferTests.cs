using System.Text;
using ServiceBusCoordinator.Core.Connections;
using Xunit;

namespace ServiceBusCoordinator.Core.Tests;

public class ConnectionTransferTests
{
    private const string Cs1 = "Endpoint=sb://one.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=abc123=";
    private const string Cs2 = "Endpoint=sb://two.servicebus.windows.net/;SharedAccessKeyName=listen;SharedAccessKey=def456=";
    private const string Cs3 = "Endpoint=sb://three.servicebus.windows.net/;SharedAccessKeyName=send;SharedAccessKey=ghi789=";

    private static List<ConnectionLayoutItem> Sample()
    {
        var folder = new ConnectionFolder { Name = "Production", IsExpanded = false };
        return
        [
            ConnectionLayoutItem.ForConnection(new ConnectionProfile { Name = "dev", ConnectionString = Cs1 }),
            ConnectionLayoutItem.ForFolder(folder,
            [
                new ConnectionProfile { Name = "prod-a", ConnectionString = Cs2, ManualEntities = ["orders", "events/subscriptions/audit"] },
                new ConnectionProfile { Name = "prod-b", ConnectionString = Cs3 },
            ]),
        ];
    }

    private static void AssertSample(IReadOnlyList<ConnectionLayoutItem> items)
    {
        Assert.Equal(2, items.Count);
        Assert.Null(items[0].Folder);
        Assert.Equal("dev", items[0].Connections.Single().Name);
        Assert.Equal(Cs1, items[0].Connections.Single().ConnectionString);
        Assert.Equal("Production", items[1].Folder!.Name);
        Assert.Equal(["prod-a", "prod-b"], items[1].Connections.Select(c => c.Name));
        Assert.Equal(["orders", "events/subscriptions/audit"], items[1].Connections[0].ManualEntities);
    }

    [Fact]
    public void Plain_export_round_trips_folders_order_and_manual_entities()
    {
        var file = ConnectionTransfer.Export(Sample());

        Assert.False(ConnectionTransfer.IsEncrypted(file));
        Assert.Contains(Cs1, Encoding.UTF8.GetString(file));
        AssertSample(ConnectionTransfer.Import(file));
    }

    [Fact]
    public void Encrypted_export_hides_secrets_and_round_trips_with_the_password()
    {
        var file = ConnectionTransfer.Export(Sample(), "correct horse");
        var text = Encoding.UTF8.GetString(file);

        Assert.True(ConnectionTransfer.IsEncrypted(file));
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("one.servicebus", text);
        Assert.DoesNotContain("Production", text);
        AssertSample(ConnectionTransfer.Import(file, "correct horse"));
    }

    [Fact]
    public void Encrypted_import_needs_the_right_password()
    {
        var file = ConnectionTransfer.Export(Sample(), "correct horse");

        Assert.Throws<UnauthorizedAccessException>(() => ConnectionTransfer.Import(file));
        Assert.Throws<UnauthorizedAccessException>(() => ConnectionTransfer.Import(file, "wrong"));
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var file = Encoding.UTF8.GetString(ConnectionTransfer.Export(Sample(), "pw-12345"));
        var start = file.IndexOf("\"data\": \"", StringComparison.Ordinal) + 9;
        var flipped = file[start] == 'A' ? 'B' : 'A';
        var tampered = Encoding.UTF8.GetBytes(file[..start] + flipped + file[(start + 1)..]);

        Assert.Throws<UnauthorizedAccessException>(() => ConnectionTransfer.Import(tampered, "pw-12345"));
    }

    [Fact]
    public void Imported_connections_get_fresh_ids()
    {
        var items = Sample();
        var imported = ConnectionTransfer.Import(ConnectionTransfer.Export(items));

        Assert.NotEqual(items[0].Connections[0].Id, imported[0].Connections[0].Id);
        Assert.NotEqual(items[1].Folder!.Id, imported[1].Folder!.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"something-else\",\"version\":1}")]
    [InlineData("[1,2,3]")]
    public void Other_files_are_rejected(string content)
    {
        Assert.Throws<InvalidDataException>(() => ConnectionTransfer.Import(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void Newer_format_versions_are_rejected_with_a_clear_message()
    {
        var file = Encoding.UTF8.GetBytes("{\"format\":\"service-bus-coordinator-connections\",\"version\":2,\"entries\":[]}");

        var ex = Assert.Throws<InvalidDataException>(() => ConnectionTransfer.Import(file));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void Missing_names_fall_back_to_the_namespace_and_blank_connection_strings_are_dropped()
    {
        var file = Encoding.UTF8.GetBytes(
            "{\"format\":\"service-bus-coordinator-connections\",\"version\":1,\"entries\":[{\"connections\":[{\"connectionString\":\"" + Cs2 + "\"},{\"name\":\"empty\",\"connectionString\":\" \"}]}]}");

        var items = ConnectionTransfer.Import(file);

        Assert.Equal("two", items.Single().Connections.Single().Name);
    }

    [Theory]
    [InlineData(Cs1, Cs1, true)]
    [InlineData(Cs1, "Endpoint=sb://one.servicebus.windows.net;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=abc123=;", true)]
    [InlineData(Cs1, "sharedaccesskey=abc123=;Endpoint=sb://one.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey", true)]
    [InlineData(Cs1, Cs2, false)]
    [InlineData(Cs1, "Endpoint=sb://one.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=other=", false)]
    public void SameConnection_ignores_formatting_but_not_credentials(string a, string b, bool same)
    {
        Assert.Equal(same, ConnectionTransfer.SameConnection(a, b));
    }
}
