using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

public class QueryRecordTests
{
    private static QueryRecord Serverinfo() =>
        Assert.Single(QueryResponseParser.Parse(Fixture.Ssh("serverinfo")).Records);

    [Fact]
    public void Reads_typed_fields_from_a_real_serverinfo_record()
    {
        var record = Serverinfo();

        Assert.Equal("TeamSpeak 6 Server", record.GetString("virtualserver_name"));
        Assert.Equal(1, record.GetInt32("virtualserver_id"));
        Assert.Equal(9987, record.GetInt32("virtualserver_port"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void Decodes_the_one_and_zero_convention_for_booleans(string raw, bool expected)
    {
        var record = new QueryRecord(new Dictionary<string, string> { ["flag"] = raw });

        Assert.Equal(expected, record.GetBoolean("flag"));
    }

    [Fact]
    public void Falls_back_when_a_field_is_absent()
    {
        var record = new QueryRecord(new Dictionary<string, string>());

        Assert.Equal("none", record.GetString("missing", "none"));
        Assert.Equal(-1, record.GetInt32("missing", -1));
        Assert.True(record.GetBoolean("missing", fallback: true));
        Assert.Null(record.GetUnixTime("missing"));
        Assert.Null(record.GetDuration("missing"));
    }

    [Fact]
    public void Treats_an_empty_value_as_absent_for_typed_reads()
    {
        // The SSH transport reports a valueless field as a bare key, so this is a real shape.
        var record = new QueryRecord(new Dictionary<string, string> { ["client_nickname"] = "" });

        Assert.Equal("unknown", record.GetString("client_nickname", "unknown"));
        Assert.True(record.ContainsKey("client_nickname"));
    }

    [Fact]
    public void Decodes_a_unix_timestamp()
    {
        var record = new QueryRecord(new Dictionary<string, string> { ["created"] = "1789339353" });

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1789339353),
            record.GetUnixTime("created"));
    }

    [Fact]
    public void Treats_a_zero_timestamp_as_never_rather_than_as_the_epoch()
    {
        var record = new QueryRecord(new Dictionary<string, string> { ["expires_at"] = "0" });

        Assert.Null(record.GetUnixTime("expires_at"));
    }

    [Fact]
    public void Decodes_a_duration_in_seconds()
    {
        var record = new QueryRecord(new Dictionary<string, string> { ["virtualserver_uptime"] = "3661" });

        Assert.Equal(TimeSpan.FromSeconds(3661), record.GetDuration("virtualserver_uptime"));
    }

    [Fact]
    public void GetRequired_names_the_available_fields_when_it_fails()
    {
        var record = new QueryRecord(new Dictionary<string, string> { ["cid"] = "1" });

        var ex = Assert.Throws<QueryProtocolException>(() => record.GetRequired("clid"));

        Assert.Contains("clid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Still_behaves_as_a_plain_dictionary()
    {
        var record = Serverinfo();

        Assert.True(record.ContainsKey("virtualserver_name"));
        Assert.Equal("TeamSpeak 6 Server", record["virtualserver_name"]);
        Assert.NotEmpty(record.Keys);
        // Enumerating the record must agree with its Count.
        Assert.Equal(record.Count, record.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count());
    }
}