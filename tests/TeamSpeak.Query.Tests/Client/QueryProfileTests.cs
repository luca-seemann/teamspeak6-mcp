using TeamSpeak.Query.Client;

namespace TeamSpeak.Query.Tests.Client;

public class QueryProfileTests
{
    [Fact]
    public void A_password_alone_enables_ssh_but_not_the_web_query()
    {
        var profile = new QueryProfile { Name = "p", Host = "h", Password = "secret" };

        Assert.True(profile.CanUseSsh);
        Assert.False(profile.CanUseWebQuery);
        profile.Validate();
    }

    [Fact]
    public void The_web_query_needs_both_a_url_and_a_key()
    {
        var urlOnly = new QueryProfile
        {
            Name = "p",
            Host = "h",
            WebQueryUrl = new Uri("http://h:10080"),
        };

        Assert.False(urlOnly.CanUseWebQuery);

        urlOnly.ApiKey = "key";
        Assert.True(urlOnly.CanUseWebQuery);
    }

    [Fact]
    public void Rejects_a_profile_with_no_usable_credentials()
    {
        var profile = new QueryProfile { Name = "p", Host = "h" };

        var ex = Assert.Throws<InvalidOperationException>(profile.Validate);

        Assert.Contains("neither", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_selecting_ssh_without_a_password()
    {
        var profile = new QueryProfile
        {
            Name = "p",
            Host = "h",
            WebQueryUrl = new Uri("http://h:10080"),
            ApiKey = "key",
            Transport = PreferredTransport.Ssh,
        };

        Assert.Throws<InvalidOperationException>(profile.Validate);
    }

    [Fact]
    public void Rejects_selecting_the_web_query_without_a_key()
    {
        var profile = new QueryProfile
        {
            Name = "p",
            Host = "h",
            Password = "secret",
            Transport = PreferredTransport.WebQuery,
        };

        Assert.Throws<InvalidOperationException>(profile.Validate);
    }

    [Fact]
    public void Defaults_match_the_servers_own_defaults()
    {
        var profile = new QueryProfile { Name = "p", Host = "h" };

        Assert.Equal(10022, profile.SshPort);
        Assert.Equal("serveradmin", profile.Username);
        Assert.Equal(1, profile.DefaultVirtualServerId);
        Assert.Equal(FloodGuard.DefaultInterval, profile.CommandInterval);
    }
}