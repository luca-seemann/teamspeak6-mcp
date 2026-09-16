using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

public class QueryResponseParserTests
{
    [Fact]
    public void Parses_a_single_record_response()
    {
        var response = QueryResponseParser.Parse(Fixture.Ssh("version"));

        var record = Assert.Single(response.Records);
        Assert.Equal("6.0.0-beta12.1", record["version"]);
        Assert.Equal("1785239375", record["build"]);
        Assert.Equal("Linux", record["platform"]);
        Assert.True(response.Error.IsSuccess);
    }

    [Fact]
    public void Splits_a_list_response_on_the_record_separator()
    {
        var response = QueryResponseParser.Parse(Fixture.Ssh("clientlist"));

        Assert.Equal(3, response.Records.Count);
        Assert.Equal(["4", "3", "1"], response.Records.Select(r => r["clid"]));
        Assert.Contains(response.Records, r => r["client_nickname"] == "Alice");
    }

    [Fact]
    public void Reads_an_empty_value_sent_as_a_bare_key()
    {
        // The server writes a valueless field as the key alone, with no '='.
        const string Raw = "virtualserver_unique_identifier client_nickname client_database_id=1\n\nerror id=0 msg=ok\n";

        var record = Assert.Single(QueryResponseParser.Parse(Raw).Records);

        Assert.Equal(string.Empty, record["virtualserver_unique_identifier"]);
        Assert.Equal(string.Empty, record["client_nickname"]);
        Assert.Equal("1", record["client_database_id"]);
    }

    [Fact]
    public void Unescapes_the_error_message_of_an_empty_result_set()
    {
        var response = QueryResponseParser.Parse(Fixture.Ssh("banlist"));

        Assert.Empty(response.Records);
        Assert.False(response.Error.IsSuccess);
        Assert.Equal(1281, response.Error.Id);
        Assert.Equal("database empty result set", response.Error.Message);
    }

    [Fact]
    public void Unescapes_values_inside_a_large_record()
    {
        var record = Assert.Single(QueryResponseParser.Parse(Fixture.Ssh("serverinfo")).Records);

        Assert.Equal("TeamSpeak 6 Server", record["virtualserver_name"]);
        Assert.Equal("files/virtualserver_1", record["virtualserver_filebase"]);
        Assert.Equal("0.0.0.0, ::", record["virtualserver_ip"]);
    }

    [Fact]
    public void Handles_a_response_carrying_only_a_status_line()
    {
        var response = QueryResponseParser.Parse("error id=1539 msg=parameter\\snot\\sfound\n");

        Assert.Empty(response.Records);
        Assert.Equal(1539, response.Error.Id);
        Assert.Equal("parameter not found", response.Error.Message);
    }

    [Theory]
    [InlineData("version=6.0.0\n", false)]
    [InlineData("version=6.0.0\n\nerror id=0 msg=ok\n", true)]
    [InlineData("", false)]
    public void Detects_whether_the_terminating_status_line_has_arrived(string raw, bool complete)
    {
        Assert.Equal(complete, QueryResponseParser.IsComplete(raw));
    }

    [Fact]
    public void Ends_a_help_page_only_at_the_status_line_that_starts_its_line()
    {
        // Measured on 6.0.0-beta12.1: lines end with \n\r, and the example quotes a status line
        // indented by two spaces before the real one.
        const string Raw =
            "Usage: apikeyadd scope={manage|write|read}\n\r\n\r" +
            "Example:\n\r\n\r  apikeyadd scope=manage\n\r\n\r  error id=0 msg=ok\n\r\n\r\n\r" +
            "error id=0 msg=ok\n\r";

        var response = QueryResponseParser.Parse(Raw);

        Assert.True(response.Error.IsSuccess);
        Assert.Equal(
            "Usage: apikeyadd scope={manage|write|read}\n\nExample:\n\n  apikeyadd scope=manage\n\n  error id=0 msg=ok",
            response.Text);
        Assert.False(QueryResponseParser.IsComplete(Raw[..Raw.LastIndexOf("error", StringComparison.Ordinal)]));
        Assert.True(QueryResponseParser.IsComplete(Raw));
    }

    [Fact]
    public void Keeps_the_payload_of_an_ordinary_response_as_text_too()
    {
        var response = QueryResponseParser.Parse(Fixture.Ssh("version"));

        Assert.Equal("version=6.0.0-beta12.1 build=1785239375 platform=Linux", response.Text);
    }

    [Fact]
    public void Rejects_a_response_without_a_status_line()
    {
        Assert.Throws<QueryProtocolException>(() => QueryResponseParser.Parse("version=6.0.0\n"));
    }
}