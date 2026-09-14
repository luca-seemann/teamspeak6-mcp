using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Hosting;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Query.Client;

namespace TeamSpeak.Mcp.Tests.Safety;

public class SafetyPolicyTests
{
    private static QueryProfile Profile(string name) => new() { Name = name, Host = "h", Password = "secret" };

    [Fact]
    public void A_profile_without_its_own_level_gets_the_default()
    {
        var policy = new SafetyPolicy(SafetyLevel.Write);

        Assert.Equal(SafetyLevel.Write, policy.LevelFor("anything"));
    }

    [Fact]
    public void A_profile_level_overrides_the_default_in_both_directions()
    {
        var policy = new SafetyPolicy(
            SafetyLevel.Write,
            new Dictionary<string, SafetyLevel>
            {
                ["staging"] = SafetyLevel.Destructive,
                ["prod"] = SafetyLevel.ReadOnly,
            });

        Assert.Equal(SafetyLevel.Destructive, policy.LevelFor("staging"));
        Assert.Equal(SafetyLevel.ReadOnly, policy.LevelFor("PROD"));
    }

    [Theory]
    [InlineData(SafetyLevel.ReadOnly)]
    [InlineData(SafetyLevel.Write)]
    public void Allows_anything_up_to_the_profile_level(SafetyLevel required)
    {
        var policy = new SafetyPolicy(SafetyLevel.Write);

        policy.Demand(Profile("p"), required, "test");
    }

    [Fact]
    public void Refuses_more_than_the_profile_allows_and_says_how_to_allow_it()
    {
        var policy = new SafetyPolicy(SafetyLevel.ReadOnly);

        var ex = Assert.Throws<McpException>(
            () => policy.Demand(Profile("prod"), SafetyLevel.Destructive, "ts_query_raw with 'channeldelete'"));

        // The model has to be able to tell the user exactly what to change.
        Assert.Contains("ts_query_raw with 'channeldelete'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Destructive", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TeamSpeak:Profiles:prod:Safety", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TSMCP_TeamSpeak__Profiles__prod__Safety", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binds_the_global_level_and_per_profile_overrides_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamSpeak:Safety"] = "Write",
                ["TeamSpeak:Profiles:prod:Host"] = "prod.example.com",
                ["TeamSpeak:Profiles:prod:Password"] = "a",
                ["TeamSpeak:Profiles:prod:Safety"] = "ReadOnly",
                ["TeamSpeak:Profiles:staging:Host"] = "staging.example.com",
                ["TeamSpeak:Profiles:staging:Password"] = "b",
                ["TeamSpeak:Profiles:staging:Safety"] = "Destructive",
                ["TeamSpeak:Profiles:lab:Host"] = "lab.example.com",
                ["TeamSpeak:Profiles:lab:Password"] = "c",
            })
            .Build();

        var services = new ServiceCollection();
        McpHostFactory.AddTeamSpeak(services, configuration);
        var policy = services.BuildServiceProvider().GetRequiredService<SafetyPolicy>();

        Assert.Equal(SafetyLevel.ReadOnly, policy.LevelFor("prod"));
        Assert.Equal(SafetyLevel.Destructive, policy.LevelFor("staging"));
        Assert.Equal(SafetyLevel.Write, policy.LevelFor("lab"));
    }

    [Fact]
    public void Defaults_to_read_only_when_nothing_is_configured()
    {
        var services = new ServiceCollection();
        McpHostFactory.AddTeamSpeak(services, new ConfigurationBuilder().Build());

        var policy = services.BuildServiceProvider().GetRequiredService<SafetyPolicy>();

        Assert.Equal(SafetyLevel.ReadOnly, policy.Default);
    }
}