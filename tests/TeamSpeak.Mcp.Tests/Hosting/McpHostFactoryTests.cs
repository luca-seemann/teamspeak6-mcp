using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

public class McpHostFactoryTests
{
    [Fact]
    public void Stdio_host_registers_the_mcp_server()
    {
        using var host = McpHostFactory.CreateStdioHost([]);

        Assert.NotNull(host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value);
    }

    [Fact]
    public void Stdio_host_declares_the_channel_only_when_it_is_asked_to()
    {
        using var plain = McpHostFactory.CreateStdioHost([]);
        using var channel = McpHostFactory.CreateStdioHost(["--TeamSpeak:Channel:Enabled=true"]);

        Assert.DoesNotContain(ChannelPush.Capability, Experimental(plain));
        Assert.Contains(ChannelPush.Capability, Experimental(channel));
    }

    [Fact]
    public void Http_app_refuses_to_serve_a_channel()
    {
        // A channel pushes into one session it holds; this transport answers each request on its own.
        var refusal = Assert.Throws<InvalidOperationException>(
            () => McpHostFactory.CreateHttpApp(["--TeamSpeak:Channel:Enabled=true"], "http://127.0.0.1:0"));

        Assert.Contains("TeamSpeak:Channel:Enabled", refusal.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Experimental(IHost host) =>
        host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.Capabilities?.Experimental?.Keys
            ?? [];

    [Fact]
    public async Task Http_app_starts_and_stops()
    {
        // Port 0 lets the OS pick a free port, so the test never collides with a running server.
        await using var app = McpHostFactory.CreateHttpApp([], "http://127.0.0.1:0");

        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}