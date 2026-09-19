using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

/// <summary>Ordering the versions a TeamSpeak server reports, including its beta series.</summary>
public class ServerVersionTests
{
    [Theory]
    [InlineData("6.0.0-beta12.1", "6.0.0-beta13")]
    [InlineData("6.0.0-beta9", "6.0.0-beta12.1")]
    [InlineData("6.0.0-beta13", "6.0.0")]
    [InlineData("6.0.0-alpha20", "6.0.0-beta1")]
    [InlineData("6.0.0", "6.0.1")]
    [InlineData("6.0", "6.0.1")]
    [InlineData("6.9.9", "7.0.0-beta1")]
    public void Orders_releases_and_their_betas(string lower, string higher)
    {
        // beta12.1 below beta13 is the case that matters: as text it sorts the other way round.
        var below = ServerVersion.Parse(lower);
        var above = ServerVersion.Parse(higher);

        Assert.True(below < above);
        Assert.True(above > below);
        Assert.False(below >= above);
    }

    [Fact]
    public void Reads_the_two_forms_a_server_reports_as_the_same_version()
    {
        // 'version' answers 6.0.0-beta13, serverinfo's virtualserver_version appends the build.
        var reported = ServerVersion.Parse("6.0.0-beta13");
        var withBuild = ServerVersion.Parse("6.0.0-beta13 [Build: 1789645103]");

        Assert.True(reported == withBuild);
        Assert.Equal(reported.GetHashCode(), withBuild.GetHashCode());
        Assert.Equal("6.0.0-beta13 [Build: 1789645103]", withBuild.Text);
        Assert.True(withBuild.IsPreRelease);
        Assert.False(ServerVersion.Parse("6.0.0").IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("beta13")]
    public void Refuses_what_it_cannot_compare(string? text)
    {
        Assert.Null(ServerVersion.TryParse(text));
    }

    [Fact]
    public void A_version_that_is_not_known_compares_below_every_version()
    {
        // KnownCrashes leans on this: a server that will not say its version gets the old, unsafe answer.
        ServerVersion? unknown = null;

        Assert.True(unknown < ServerVersion.Parse("6.0.0-beta13"));
        Assert.False(unknown >= ServerVersion.Parse("6.0.0-beta13"));
        Assert.True(unknown == null);
    }

    [Fact]
    public void Equal_versions_compare_equal_whatever_the_object()
    {
        var version = ServerVersion.Parse("6.0.0-beta13");

        Assert.Equal(version, ServerVersion.Parse("6.0.0-beta13"));
        Assert.NotEqual(version, ServerVersion.Parse("6.0.0-beta14"));
        Assert.False(version.Equals("6.0.0-beta13"));
        Assert.Equal(0, version.CompareTo(ServerVersion.Parse("6.0.0-beta13")));
        Assert.Equal(1, version.CompareTo(null));
    }
}
