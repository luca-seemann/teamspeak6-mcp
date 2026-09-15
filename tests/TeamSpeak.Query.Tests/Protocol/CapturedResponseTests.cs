using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

/// <summary>
/// Decodes responses captured on 15 September 2026 from the live 6.0.0-beta12.1 test server, over SSH
/// and the WebQuery within the same minute, for commands the first fixtures did not cover.
/// </summary>
/// <remarks>
/// Unlike the first fixtures, these SSH captures are byte for byte what the server sent, including the
/// <c>\n\r</c> it ends every line with.
/// </remarks>
public class CapturedResponseTests
{
    /// <summary>Commands whose records do not change between two captures a few seconds apart.</summary>
    public static TheoryData<string> StableCaptures() =>
    [
        "bindinglist",
        "channelfind",
        "channelpermlist_permsid",
        "clientdbfind",
        "clientdbinfo",
        "logview",
        "permfind_talkpower",
        "permidgetbyname_talkpower",
        "permoverview_cid1_all",
        "servergroupclientlist_names",
        "servergrouppermlist_permsid",
        "servergroupsbyclientid",
    ];

    private static QueryResponse Ssh(string name) => QueryResponseParser.Parse(Fixture.Ssh(name));

    private static QueryResponse Http(string name) => WebQueryResponseParser.Parse(Fixture.Http(name));

    private static List<List<KeyValuePair<string, string>>> Flatten(QueryResponse response) =>
        response.Records
            .Select(record => record.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList())
            .ToList();

    [Theory]
    [MemberData(nameof(StableCaptures))]
    public void Decodes_to_the_same_records_over_both_transports(string name)
    {
        var ssh = Ssh(name);
        var http = Http(name);

        Assert.True(ssh.Error.IsSuccess, ssh.Error.Message);
        Assert.True(http.Error.IsSuccess, http.Error.Message);
        Assert.NotEmpty(ssh.Records);
        Assert.Equal(Flatten(ssh), Flatten(http));
    }

    [Fact]
    public void The_ssh_captures_carry_the_line_ending_the_server_really_sends() =>
        Assert.Contains("\n\rerror id=0 msg=ok\n\r", Fixture.Ssh("bindinglist"), StringComparison.Ordinal);

    [Fact]
    public void Bindinglist_lists_every_address_the_instance_listens_on() =>
        Assert.Equal(["0.0.0.0", "::"], Ssh("bindinglist").Records.Select(record => record["ip"]));

    [Fact]
    public void Permfind_marks_a_channel_group_assigned_in_no_particular_channel_with_channel_zero()
    {
        var holders = Ssh("permfind_talkpower").Records
            .Select(record => (record["t"], record["id1"], record["id2"], record["p"]))
            .ToList();

        Assert.Equal(
            [("0", "6", "0", "226"), ("3", "0", "5", "226"), ("3", "0", "6", "226"), ("3", "0", "7", "226")],
            holders);
    }

    [Fact]
    public void Permoverview_gives_every_row_its_source_and_the_value_with_its_flags()
    {
        var rows = Ssh("permoverview_cid1_all").Records;

        Assert.True(rows.Count > 100, $"Only {rows.Count} rows.");
        Assert.All(rows, row => Assert.Equal(["id1", "id2", "n", "p", "s", "t", "v"], row.Keys.Order(StringComparer.Ordinal)));
        Assert.Contains(rows, row => row["t"] == "0" && row["id1"] == "6");
    }

    [Fact]
    public void Clientdbinfo_reads_bare_keys_as_empty_and_keeps_padding_in_the_unique_identity()
    {
        var client = Assert.Single(Ssh("clientdbinfo").Records);

        Assert.Equal(string.Empty, client["client_flag_avatar"]);
        Assert.Equal(string.Empty, client["client_description"]);
        Assert.Equal("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ=", client["client_unique_identifier"]);
        Assert.Equal("Alice", client["client_nickname"]);
        Assert.Equal(785932739L, client.GetInt64("client_total_bytes_uploaded"));
    }

    [Fact]
    public void Servergroupsbyclientid_unescapes_the_group_name() =>
        Assert.Equal("Server Admin", Assert.Single(Ssh("servergroupsbyclientid").Records)["name"]);

    [Fact]
    public void Servergrouppermlist_with_permsid_names_each_permission_instead_of_its_id()
    {
        var rows = Ssh("servergrouppermlist_permsid").Records;

        Assert.True(rows.Count > 100, $"Only {rows.Count} rows.");
        Assert.All(rows, row =>
        {
            Assert.Equal("6", row["sgid"]);
            Assert.Matches("^[bi]_", row["permsid"]);
            Assert.DoesNotContain("permid", row.Keys);
        });
    }

    [Fact]
    public void Logview_puts_the_position_and_file_size_on_the_first_entry_only_and_unescapes_the_line()
    {
        var entries = Ssh("logview").Records;

        Assert.Equal(5, entries.Count);
        Assert.Equal("15273", entries[0]["last_pos"]);
        Assert.Equal("16127", entries[0]["file_size"]);
        Assert.All(entries.Skip(1), entry => Assert.DoesNotContain("last_pos", entry.Keys));
        Assert.StartsWith(
            "2026-09-15 09:04:09.521235|INFO    |VirtualServer |1  |permission 'i_client_talk_power'(id:226)",
            entries[0]["l"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Serverrequestconnectioninfo_has_the_same_fields_over_both_transports()
    {
        // The counters move between captures, so only the shape is compared.
        var ssh = Assert.Single(Ssh("serverrequestconnectioninfo").Records);
        var http = Assert.Single(Http("serverrequestconnectioninfo").Records);

        Assert.Equal(ssh.Keys.Order(StringComparer.Ordinal), http.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("1.0000", ssh["connection_ping"]);
        Assert.Equal(0.0, ssh.GetDouble("connection_packetloss_total"));
    }

    [Fact]
    public void Apikeylist_shows_no_key_and_agrees_over_both_transports_apart_from_the_time_left()
    {
        var ssh = Assert.Single(Ssh("apikeylist").Records);
        var http = Assert.Single(Http("apikeylist").Records);

        Assert.DoesNotContain("apikey", ssh.Keys);
        Assert.Equal(string.Empty, ssh["custom_id"]);
        Assert.Equal(
            ssh.Where(pair => pair.Key != "time_left").OrderBy(pair => pair.Key, StringComparer.Ordinal),
            http.Where(pair => pair.Key != "time_left").OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }
}