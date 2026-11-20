using System.Text;
using ServiceBusCoordinator.Core.Connections;

namespace ServiceBusCoordinator.Core.Tests;

public class ConnectionLayoutTests
{
    private static ConnectionProfile P(string name, Guid? folder = null) => new() { Name = name, ConnectionString = "x", FolderId = folder };

    private static string Describe(IEnumerable<ConnectionLayoutItem> items) => string.Join(" | ", items.Select(i =>
        i.Folder is { } f ? $"[{f.Name}: {string.Join(",", i.Connections.Select(c => c.Name))}]" : i.Connections[0].Name));

    [Fact]
    public void Round_trips_folders_and_order()
    {
        var prod = new ConnectionFolder { Name = "prod", IsExpanded = false };
        var a = P("a"); var b = P("b"); var c = P("c"); var d = P("d");
        var items = new[]
        {
            ConnectionLayoutItem.ForConnection(b),
            ConnectionLayoutItem.ForFolder(prod, [d, a]),
            ConnectionLayoutItem.ForConnection(c),
        };
        var repo = new ConnectionRepository(new InMemorySecretStore());

        repo.SaveLayout(ConnectionLayout.FromItems(items));
        var loaded = repo.LoadLayout();

        Assert.Equal("b | [prod: d,a] | c", Describe(loaded.Arrange()));
        Assert.False(Assert.Single(loaded.Folders).IsExpanded);
        Assert.Equal(prod.Id, loaded.Connections.Single(x => x.Name == "a").FolderId);
        Assert.Null(loaded.Connections.Single(x => x.Name == "c").FolderId);
    }

    [Fact]
    public void Version_1_file_loads_every_connection_at_the_top_level()
    {
        var store = new InMemorySecretStore();
        store.Write(Encoding.UTF8.GetBytes("""{"Version":1,"Connections":[{"Name":"one","ConnectionString":"x"},{"Name":"two","ConnectionString":"y"}]}"""));

        var layout = new ConnectionRepository(store).LoadLayout();

        Assert.Equal("one | two", Describe(layout.Arrange()));
    }

    [Fact]
    public void Entries_missing_from_the_order_are_appended_and_orphans_go_to_the_top_level()
    {
        var known = new ConnectionFolder { Name = "known" };
        var unlisted = new ConnectionFolder { Name = "unlisted" };
        var a = P("a", known.Id);
        var orphan = P("orphan", Guid.NewGuid());
        var root = P("root");
        var layout = new ConnectionLayout([a, orphan, root], [known, unlisted], [root.Id, Guid.NewGuid(), known.Id, root.Id]);

        Assert.Equal("root | [known: a] | [unlisted: ] | orphan", Describe(layout.Arrange()));
    }

    [Fact]
    public void Plain_save_keeps_the_given_order_without_folders()
    {
        var repo = new ConnectionRepository(new InMemorySecretStore());
        var first = P("first", Guid.NewGuid());
        repo.Save([P("second"), first]);

        Assert.Equal("second | first", Describe(repo.LoadLayout().Arrange()));
        Assert.Null(first.FolderId);
    }
}
