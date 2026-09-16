using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

public class QueryCommandSerializerTests
{
    [Fact]
    public void Renders_a_bare_command()
    {
        Assert.Equal("version", QueryCommandSerializer.ToWireLine(new QueryCommand("version")));
    }

    [Fact]
    public void Escapes_parameter_values_on_the_wire()
    {
        var command = new QueryCommand(
            "channeledit",
            new Dictionary<string, string> { ["cid"] = "1", ["channel_name"] = "My Channel" });

        Assert.Equal(@"channeledit cid=1 channel_name=My\sChannel", QueryCommandSerializer.ToWireLine(command));
    }

    [Fact]
    public void Renders_options_with_a_single_leading_dash()
    {
        var command = new QueryCommand("clientlist", Options: ["uid", "-away", "groups"]);

        Assert.Equal("clientlist -uid -away -groups", QueryCommandSerializer.ToWireLine(command));
    }

    [Fact]
    public void Round_trips_through_the_parser()
    {
        var command = new QueryCommand(
            "sendtextmessage",
            new Dictionary<string, string> { ["msg"] = "hello | world / done" });

        // Feed the rendered line back through the parser as if the server had echoed it.
        var line = QueryCommandSerializer.ToWireLine(command);
        var record = Assert.Single(
            QueryResponseParser.Parse(line["sendtextmessage ".Length..] + "\nerror id=0 msg=ok\n").Records);

        Assert.Equal("hello | world / done", record["msg"]);
    }

    [Fact]
    public void Scopes_a_web_query_path_to_a_virtual_server()
    {
        var command = new QueryCommand("clientinfo", new Dictionary<string, string> { ["clid"] = "3" });

        Assert.Equal("1/clientinfo?clid=3", QueryCommandSerializer.ToWebQueryPath(command, 1));
    }

    [Fact]
    public void Omits_the_virtual_server_for_instance_wide_commands()
    {
        Assert.Equal("serverlist", QueryCommandSerializer.ToWebQueryPath(new QueryCommand("serverlist")));
    }

    [Fact]
    public void Percent_encodes_web_query_values_rather_than_escaping_them()
    {
        var command = new QueryCommand(
            "channeledit",
            new Dictionary<string, string> { ["channel_name"] = "My Channel" });

        // The line protocol would write My\sChannel here; HTTP must not.
        Assert.Equal("1/channeledit?channel_name=My%20Channel", QueryCommandSerializer.ToWebQueryPath(command, 1));
    }

    [Fact]
    public void Joins_multiple_web_query_parameters_and_options()
    {
        var command = new QueryCommand(
            "clientlist",
            new Dictionary<string, string> { ["cid"] = "1" },
            ["uid"]);

        Assert.Equal("1/clientlist?cid=1&-uid", QueryCommandSerializer.ToWebQueryPath(command, 1));
    }

    [Theory]
    [InlineData("version\nserverstop")]
    [InlineData("two words")]
    [InlineData("semi;colon")]
    [InlineData("")]
    public void Rejects_a_command_name_that_could_smuggle_a_second_command(string name)
    {
        var command = new QueryCommand(name);

        Assert.Throws<ArgumentException>(() => QueryCommandSerializer.ToWireLine(command));
        Assert.Throws<ArgumentException>(() => QueryCommandSerializer.ToWebQueryPath(command));
    }

    [Fact]
    public void Writes_positional_arguments_straight_after_the_command_name()
    {
        Assert.Equal("help channeledit", QueryCommandSerializer.ToWireLine(new QueryCommand("help", Arguments: ["channeledit"])));
    }

    [Theory]
    [InlineData("channeledit\nserverstop")]
    [InlineData("two words")]
    [InlineData("")]
    public void Rejects_an_argument_that_could_smuggle_a_second_command(string argument)
    {
        Assert.Throws<ArgumentException>(() =>
            QueryCommandSerializer.ToWireLine(new QueryCommand("help", Arguments: [argument])));
    }

    [Fact]
    public void Refuses_positional_arguments_on_the_web_query()
    {
        Assert.Throws<NotSupportedException>(() =>
            QueryCommandSerializer.ToWebQueryPath(new QueryCommand("help", Arguments: ["channeledit"])));
    }

    [Fact]
    public void Rejects_a_parameter_name_that_could_smuggle_a_second_command()
    {
        var command = new QueryCommand(
            "serveredit",
            new Dictionary<string, string> { ["cid=1 serverstop sid"] = "1" });

        Assert.Throws<ArgumentException>(() => QueryCommandSerializer.ToWireLine(command));
    }
}