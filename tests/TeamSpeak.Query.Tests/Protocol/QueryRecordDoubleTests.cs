using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

public class QueryRecordDoubleTests
{
    private static QueryRecord Record(string value) =>
        new(new Dictionary<string, string> { ["virtualserver_total_ping"] = value });

    [Theory]
    [InlineData("0.0000", 0.0)]
    [InlineData("12.5000", 12.5)]
    [InlineData("0.0150", 0.015)]
    public void Reads_the_decimal_values_the_server_writes(string raw, double expected) =>
        Assert.Equal(expected, Record(raw).GetDouble("virtualserver_total_ping"), 6);

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    public void Falls_back_on_anything_that_is_not_a_number(string raw) =>
        Assert.Equal(-1, Record(raw).GetDouble("virtualserver_total_ping", -1));

    [Fact]
    public void Falls_back_when_the_field_is_absent() =>
        Assert.Equal(-1, Record("1").GetDouble("missing", -1));
}