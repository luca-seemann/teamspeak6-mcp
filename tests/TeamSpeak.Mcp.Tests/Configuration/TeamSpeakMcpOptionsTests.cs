using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Hosting;
using TeamSpeak.Query.Client;

namespace TeamSpeak.Mcp.Tests.Configuration;

public class TeamSpeakMcpOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ProfileRegistry Build(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        McpHostFactory.AddTeamSpeak(services, Config(values));
        return services.BuildServiceProvider().GetRequiredService<ProfileRegistry>();
    }

    [Fact]
    public void Binds_a_profile_and_names_it_after_its_configuration_key()
    {
        var registry = Build(new Dictionary<string, string?>
        {
            ["TeamSpeak:Profiles:prod:Host"] = "ts.example.com",
            ["TeamSpeak:Profiles:prod:Password"] = "secret",
        });

        var profile = registry.Resolve("prod");

        Assert.Equal("prod", profile.Name);
        Assert.Equal("ts.example.com", profile.Host);
        Assert.Equal(10022, profile.SshPort);
    }

    [Fact]
    public void Binds_several_profiles()
    {
        var registry = Build(new Dictionary<string, string?>
        {
            ["TeamSpeak:Profiles:prod:Host"] = "prod.example.com",
            ["TeamSpeak:Profiles:prod:Password"] = "a",
            ["TeamSpeak:Profiles:staging:Host"] = "staging.example.com",
            ["TeamSpeak:Profiles:staging:Password"] = "b",
        });

        Assert.Equal(2, registry.Names.Count);
        Assert.Equal("staging.example.com", registry.Resolve("staging").Host);
    }

    [Fact]
    public void Converts_plain_settings_into_their_typed_forms()
    {
        var registry = Build(new Dictionary<string, string?>
        {
            ["TeamSpeak:Profiles:p:Host"] = "h",
            ["TeamSpeak:Profiles:p:WebQueryUrl"] = "http://h:10080",
            ["TeamSpeak:Profiles:p:ApiKey"] = "key",
            ["TeamSpeak:Profiles:p:CommandIntervalMs"] = "500",
            ["TeamSpeak:Profiles:p:CommandTimeoutSeconds"] = "45",
        });

        var profile = registry.Resolve("p");

        Assert.Equal(new Uri("http://h:10080"), profile.WebQueryUrl);
        Assert.Equal(TimeSpan.FromMilliseconds(500), profile.CommandInterval);
        Assert.Equal(TimeSpan.FromSeconds(45), profile.CommandTimeout);
    }

    [Fact]
    public void Rejects_an_unusable_web_query_url_by_naming_the_profile_and_the_value()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new Dictionary<string, string?>
        {
            ["TeamSpeak:Profiles:p:Host"] = "h",
            ["TeamSpeak:Profiles:p:Password"] = "secret",
            ["TeamSpeak:Profiles:p:WebQueryUrl"] = "not a url",
        }));

        Assert.Contains("'p'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a url", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_profile_with_no_host_at_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new Dictionary<string, string?>
        {
            ["TeamSpeak:Profiles:p:Password"] = "secret",
        }));

        Assert.Contains("Host", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_to_read_only()
    {
        var options = new TeamSpeakMcpOptions();

        Assert.Equal(SafetyLevel.ReadOnly, options.Safety);
    }

    [Fact]
    public void An_environment_variable_overrides_the_file()
    {
        // Secrets belong in the environment, so this override has to work.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamSpeak:Profiles:prod:Host"] = "ts.example.com",
                ["TeamSpeak:Profiles:prod:Password"] = "from-file",
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamSpeak:Profiles:prod:Password"] = "from-environment",
            })
            .Build();

        var services = new ServiceCollection();
        McpHostFactory.AddTeamSpeak(services, configuration);
        var registry = services.BuildServiceProvider().GetRequiredService<ProfileRegistry>();

        Assert.Equal("from-environment", registry.Resolve("prod").Password);
    }
}