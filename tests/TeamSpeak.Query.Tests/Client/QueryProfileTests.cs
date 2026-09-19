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
    public void The_guest_login_needs_no_credentials_on_either_interface()
    {
        // Measured on 6.0.0-beta13: SSH takes the user 'guest' with any password, and the WebQuery
        // answers a request without an x-api-key header as the same guest.
        var profile = new QueryProfile
        {
            Name = "p",
            Host = "h",
            Username = QueryProfile.GuestUsername,
            WebQueryUrl = new Uri("http://h:10080"),
        };

        Assert.True(profile.IsGuest);
        Assert.True(profile.CanUseSsh);
        Assert.True(profile.CanUseWebQuery);
        profile.Validate();

        profile.WebQueryUrl = null;
        profile.Validate();
    }

    [Theory]
    [InlineData("serveradmin", null, null)]
    [InlineData("guest", "secret", null)]
    [InlineData("guest", null, "key")]
    public void Only_an_explicit_guest_login_reaches_a_server_without_credentials(string user, string? password, string? apiKey)
    {
        // A forgotten password or API key must stay an error rather than quietly become a guest
        // session that can read next to nothing.
        var profile = new QueryProfile
        {
            Name = "p",
            Host = "h",
            Username = user,
            Password = password,
            ApiKey = apiKey,
        };

        Assert.False(profile.IsGuest);
    }

    [Fact]
    public void A_guest_profile_says_so_in_the_message_when_nothing_is_configured()
    {
        var profile = new QueryProfile { Name = "p", Host = "h" };

        var ex = Assert.Throws<InvalidOperationException>(profile.Validate);

        Assert.Contains(QueryProfile.GuestUsername, ex.Message, StringComparison.Ordinal);
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