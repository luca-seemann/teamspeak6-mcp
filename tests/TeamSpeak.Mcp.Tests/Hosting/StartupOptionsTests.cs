using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

public class StartupOptionsTests
{
    [Fact]
    public void Defaults_to_stdio_on_the_loopback_url()
    {
        var options = StartupOptions.Parse([]);

        Assert.Equal(TransportMode.Stdio, options.Transport);
        Assert.Equal(StartupOptions.DefaultHttpUrl, options.HttpUrl);
    }

    [Theory]
    [InlineData("stdio", TransportMode.Stdio)]
    [InlineData("http", TransportMode.Http)]
    [InlineData("HTTP", TransportMode.Http)]
    public void Parses_the_transport_case_insensitively(string value, TransportMode expected)
    {
        Assert.Equal(expected, StartupOptions.Parse(["--transport", value]).Transport);
    }

    [Fact]
    public void Parses_a_custom_http_url()
    {
        var options = StartupOptions.Parse(["--transport", "http", "--url", "http://0.0.0.0:9000"]);

        Assert.Equal(TransportMode.Http, options.Transport);
        Assert.Equal("http://0.0.0.0:9000", options.HttpUrl);
    }

    [Fact]
    public void Rejects_an_unknown_transport()
    {
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--transport", "telnet"]));
    }

    [Fact]
    public void Rejects_an_option_without_a_value()
    {
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--transport"]));
    }

    [Fact]
    public void Rejects_an_unrecognised_argument()
    {
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--nope"]));
    }
}