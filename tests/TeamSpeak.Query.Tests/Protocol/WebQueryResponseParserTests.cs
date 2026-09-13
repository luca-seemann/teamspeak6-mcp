using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

public class WebQueryResponseParserTests
{
    [Fact]
    public void Parses_a_single_record_envelope()
    {
        var response = WebQueryResponseParser.Parse(Fixture.Http("channelinfo_cid1"));

        var record = Assert.Single(response.Records);
        Assert.Equal("Default Channel", record["channel_name"]);
        Assert.True(response.Error.IsSuccess);
    }

    [Fact]
    public void Surfaces_a_flood_rejection_with_the_wait_the_server_asked_for()
    {
        // Captured by accident while hammering the server, which makes it the most authentic
        // fixture in the set.
        var error = WebQueryResponseParser.Parse(Fixture.Http("flooding")).Error;

        Assert.True(error.IsFlooding);
        Assert.Equal(QueryErrorCode.Flooding, error.Id);
        Assert.Equal("client is flooding", error.Message);
        Assert.Equal(TimeSpan.FromSeconds(1), error.RetryAfter);
    }

    [Fact]
    public void Treats_a_missing_body_as_an_empty_result_rather_than_an_error()
    {
        // An empty result set omits "body" entirely instead of sending [].
        var response = WebQueryResponseParser.Parse(Fixture.Http("banlist"));

        Assert.Empty(response.Records);
        Assert.Equal(1281, response.Error.Id);
        Assert.Equal("database empty result set", response.Error.Message);
    }

    [Fact]
    public void Carries_the_extra_message_when_the_server_sends_one()
    {
        const string Json =
            """{"status":{"code":1538,"extra_message":"unknown command","message":"invalid parameter"}}""";

        var error = WebQueryResponseParser.Parse(Json).Error;

        Assert.Equal(1538, error.Id);
        Assert.Equal("invalid parameter", error.Message);
        Assert.Equal("unknown command", error.ExtraMessage);
    }

    [Fact]
    public void Represents_an_empty_value_as_an_empty_string()
    {
        const string Json =
            """{"body":[{"virtualserver_machine_id":"","virtualserver_id":"1"}],"status":{"code":0,"message":"ok"}}""";

        var record = Assert.Single(WebQueryResponseParser.Parse(Json).Records);

        Assert.Equal(string.Empty, record["virtualserver_machine_id"]);
        Assert.Equal("1", record["virtualserver_id"]);
    }

    [Fact]
    public void Rejects_a_payload_that_is_not_valid_json()
    {
        Assert.Throws<QueryProtocolException>(() => WebQueryResponseParser.Parse("not json"));
    }

    [Fact]
    public void Rejects_an_envelope_without_a_status_object()
    {
        Assert.Throws<QueryProtocolException>(() => WebQueryResponseParser.Parse("""{"body":[]}"""));
    }
}