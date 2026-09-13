using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

/// <summary>
/// Both interfaces expose the same command set, so the same command captured over SSH and over
/// WebQuery has to decode to the same <see cref="QueryResponse"/>. Everything above the transport
/// boundary depends on that, which is what lets a tool be written once and work over either.
/// </summary>
/// <remarks>
/// The fixtures behind these tests were captured minutes apart from the same live server, so fields
/// that move on their own — client counts, uptimes, timestamps — are compared loosely or skipped.
/// </remarks>
public class CrossTransportParityTests
{
    [Fact]
    public void Channelinfo_decodes_to_the_same_fields_over_both_transports()
    {
        var ssh = Assert.Single(QueryResponseParser.Parse(Fixture.Ssh("channelinfo_cid1")).Records);
        var http = Assert.Single(WebQueryResponseParser.Parse(Fixture.Http("channelinfo_cid1")).Records);

        // Only 'seconds_empty' moves between captures; everything else must agree exactly,
        // including values the SSH transport had to unescape.
        var volatileKeys = new[] { "seconds_empty" };
        Assert.Equal(
            ssh.Where(p => !volatileKeys.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal),
            http.Where(p => !volatileKeys.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void An_empty_result_set_looks_the_same_over_both_transports()
    {
        // The wire forms could hardly differ more: SSH sends an escaped error line and no payload,
        // WebQuery sends JSON with no "body" key at all.
        var ssh = QueryResponseParser.Parse(Fixture.Ssh("banlist"));
        var http = WebQueryResponseParser.Parse(Fixture.Http("banlist"));

        Assert.Empty(ssh.Records);
        Assert.Empty(http.Records);
        Assert.Equal(ssh.Error.Id, http.Error.Id);
        Assert.Equal(ssh.Error.Message, http.Error.Message);
    }

    [Fact]
    public void Channellist_yields_the_same_channels_over_both_transports()
    {
        var ssh = QueryResponseParser.Parse(Fixture.Ssh("channellist"));
        var http = WebQueryResponseParser.Parse(Fixture.Http("channellist"));

        Assert.Equal(ssh.Records.Count, http.Records.Count);
        Assert.Equal(
            ssh.Records.Select(r => (r["cid"], r["channel_name"])),
            http.Records.Select(r => (r["cid"], r["channel_name"])));
    }

    [Fact]
    public void Servergrouplist_yields_the_same_groups_over_both_transports()
    {
        var ssh = QueryResponseParser.Parse(Fixture.Ssh("servergrouplist"));
        var http = WebQueryResponseParser.Parse(Fixture.Http("servergrouplist"));

        Assert.Equal(
            ssh.Records.Select(r => (r["sgid"], r["name"])).OrderBy(x => x.Item1, StringComparer.Ordinal),
            http.Records.Select(r => (r["sgid"], r["name"])).OrderBy(x => x.Item1, StringComparer.Ordinal));
    }
}