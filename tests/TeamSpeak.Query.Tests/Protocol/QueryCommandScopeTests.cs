using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

public class QueryCommandScopeTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("serverlist")]
    [InlineData("whoami")]
    [InlineData("gm")]
    public void Treats_instance_commands_as_instance_wide(string name) =>
        Assert.True(QueryCommandScope.IsInstanceWide(name));

    [Theory]
    [InlineData("serverstart")]
    [InlineData("serverstop")]
    [InlineData("serverdelete")]
    public void Treats_lifecycle_commands_as_instance_wide_so_no_use_precedes_them(string name)
    {
        // A "use" on a stopped virtual server fails, which would make serverstart impossible.
        Assert.True(QueryCommandScope.IsInstanceWide(name));
    }

    [Theory]
    [InlineData("channellist")]
    [InlineData("clientinfo")]
    [InlineData("serverinfo")]
    [InlineData("apikeyadd")]
    [InlineData("apikeydel")]
    [InlineData("some-future-command")]
    public void Treats_everything_else_as_scoped(string name) =>
        Assert.False(QueryCommandScope.IsInstanceWide(name));

    [Fact]
    public void Ignores_case() =>
        Assert.True(QueryCommandScope.IsInstanceWide("ServerList"));
}