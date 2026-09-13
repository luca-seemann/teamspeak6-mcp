using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

public class QueryEscapingTests
{
    [Theory]
    [InlineData("TeamSpeak 6 Server", @"TeamSpeak\s6\sServer")]
    [InlineData("files/virtualserver_1", @"files\/virtualserver_1")]
    [InlineData("a|b", @"a\pb")]
    [InlineData("back\\slash", @"back\\slash")]
    [InlineData("tab\there", @"tab\there")]
    [InlineData("line\nbreak", @"line\nbreak")]
    [InlineData("0.0.0.0, ::", @"0.0.0.0,\s::")]
    [InlineData("nothing-special", "nothing-special")]
    [InlineData("", "")]
    public void Escapes_and_unescapes_symmetrically(string raw, string escaped)
    {
        Assert.Equal(escaped, QueryEscaping.Escape(raw));
        Assert.Equal(raw, QueryEscaping.Unescape(escaped));
    }

    [Fact]
    public void Unescapes_a_welcome_message_captured_from_the_server()
    {
        const string Captured =
            @"Welcome\sto\sTeamSpeak,\scheck\s[URL]www.teamspeak.com[\/URL]\sfor\slatest\sinformation";

        Assert.Equal(
            "Welcome to TeamSpeak, check [URL]www.teamspeak.com[/URL] for latest information",
            QueryEscaping.Unescape(Captured));
    }

    [Theory]
    [InlineData(@"trailing\", @"trailing\")]
    [InlineData(@"unknown\qescape", @"unknown\qescape")]
    public void Passes_malformed_escapes_through_rather_than_dropping_them(string input, string expected)
    {
        Assert.Equal(expected, QueryEscaping.Unescape(input));
    }

    [Fact]
    public void Round_trips_every_escapable_character_at_once()
    {
        const string Raw = "\\ / | \a\b\f\n\r\t\v";

        Assert.Equal(Raw, QueryEscaping.Unescape(QueryEscaping.Escape(Raw)));
    }

    [Fact]
    public void Returns_the_same_instance_when_nothing_needs_escaping()
    {
        var value = "no-escapes-here";

        Assert.Same(value, QueryEscaping.Escape(value));
        Assert.Same(value, QueryEscaping.Unescape(value));
    }
}