using TeamSpeak.Query.Client;

namespace TeamSpeak.Query.Tests.Client;

public class QueryProfileKeepAliveTests
{
    [Fact]
    public void Keeps_alive_well_inside_the_measured_idle_timeout_by_default() =>
        Assert.True(new QueryProfile { Name = "p", Host = "h", Password = "secret" }.KeepAliveInterval < TimeSpan.FromSeconds(25));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Refuses_a_keepalive_interval_that_is_not_above_zero(int seconds)
    {
        var profile = new QueryProfile { Name = "p", Host = "h", Password = "secret", KeepAliveInterval = TimeSpan.FromSeconds(seconds) };

        var refused = Assert.Throws<InvalidOperationException>(profile.Validate);

        Assert.Contains("keepalive", refused.Message, StringComparison.Ordinal);
    }
}