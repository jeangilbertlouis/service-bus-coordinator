using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using ServiceBusCoordinator.Core.Explorer;

namespace ServiceBusCoordinator.Core.Tests;

public class FormatTests
{
    [Fact]
    public void Max_timespan_is_never() => Assert.Equal("Never", Format.Duration(TimeSpan.MaxValue));

    [Theory]
    [InlineData(0, 0, 0, 0, "0 s")]
    [InlineData(0, 0, 1, 0, "1 min")]
    [InlineData(0, 0, 0, 30, "30 s")]
    [InlineData(14, 0, 0, 0, "14 d")]
    [InlineData(1, 2, 3, 4, "1 d 2 h 3 min 4 s")]
    public void Durations(int d, int h, int m, int s, string expected) =>
        Assert.Equal(expected, Format.Duration(new TimeSpan(d, h, m, s)));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void Bytes(long bytes, string expected) => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void Sizes()
    {
        Assert.Equal("1 GB", Format.Megabytes(1024));
        Assert.Equal("5 GB", Format.Megabytes(5120));
        Assert.Equal("500 MB", Format.Megabytes(500));
        Assert.Equal("256 KB", Format.Kilobytes(256));
        Assert.Equal("100 MB", Format.Kilobytes(102400));
        Assert.Equal("Default", Format.Kilobytes(null));
    }

    [Fact]
    public void Text_and_dates()
    {
        Assert.Equal("—", Format.Text(""));
        Assert.Equal("—", Format.Text(null));
        Assert.Equal("orders", Format.Text("orders"));
        Assert.Equal("—", Format.Date(default));
    }
}

public class RuleDescriptionTests
{
    [Fact]
    public void True_filter()
    {
        var r = EntityDetails.DescribeRule(ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter()));
        Assert.Equal(("$Default", "All messages", "1=1", (string?)null), (r.Name, r.FilterKind, r.Filter, r.Action));
    }

    [Fact]
    public void Sql_filter_with_action()
    {
        var r = EntityDetails.DescribeRule(ServiceBusModelFactory.RuleProperties(
            "big", new SqlRuleFilter("amount > 100"), new SqlRuleAction("SET priority = 'high'")));
        Assert.Equal("SQL", r.FilterKind);
        Assert.Equal("amount > 100", r.Filter);
        Assert.Equal("SET priority = 'high'", r.Action);
    }

    [Fact]
    public void Correlation_filter_lists_set_fields()
    {
        var filter = new CorrelationRuleFilter { Subject = "order.created", CorrelationId = "c1" };
        filter.ApplicationProperties["tenant"] = "acme";

        var r = EntityDetails.DescribeRule(ServiceBusModelFactory.RuleProperties("orders", filter));

        Assert.Equal("Correlation", r.FilterKind);
        Assert.Equal("CorrelationId = 'c1' AND Subject = 'order.created' AND tenant = 'acme'", r.Filter);
    }

    [Fact]
    public void Empty_correlation_filter() =>
        Assert.Equal("(matches everything)", EntityDetails.DescribeRule(ServiceBusModelFactory.RuleProperties("x", new CorrelationRuleFilter())).Filter);
}
