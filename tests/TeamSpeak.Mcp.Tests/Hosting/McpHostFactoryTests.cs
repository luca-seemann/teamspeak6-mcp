using Microsoft.Extensions.DependencyInjection;
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
    public async Task Http_app_starts_and_stops()
    {
        // Port 0 lets the OS pick a free port, so the test never collides with a running server.
        await using var app = McpHostFactory.CreateHttpApp([], "http://127.0.0.1:0");

        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}