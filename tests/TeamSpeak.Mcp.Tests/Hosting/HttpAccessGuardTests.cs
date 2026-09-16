using System.Net;
using System.Text;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

/// <summary>Who may reach the Streamable HTTP endpoint, tested against a real listening app.</summary>
public class HttpAccessGuardTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts the app on a free loopback port, sends one initialize and returns the status.</summary>
    private static async Task<HttpStatusCode> PostAsync(string[] args, Action<HttpRequestMessage>? configure = null)
    {
        await using var app = McpHostFactory.CreateHttpApp(args, "http://127.0.0.1:0");
        await app.StartAsync(Ct);

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, address + "/")
            {
                Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
            configure?.Invoke(request);

            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }
        finally
        {
            await app.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Accepts_a_client_that_sends_no_origin()
    {
        Assert.Equal(HttpStatusCode.OK, await PostAsync([]));
    }

    [Fact]
    public async Task Refuses_a_web_page_from_another_origin()
    {
        // Measured before the guard existed: this request was answered with 200.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            await PostAsync([], request => request.Headers.TryAddWithoutValidation("Origin", "http://evil.example")));
    }

    [Fact]
    public async Task Refuses_a_foreign_host_name_as_a_rebound_page_would_send()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync([], request => request.Headers.Host = "evil.example"));
    }

    [Fact]
    public async Task Accepts_a_page_served_from_a_loopback_origin()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            await PostAsync([], request => request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173")));
    }

    [Fact]
    public async Task Accepts_a_configured_origin_and_host()
    {
        Assert.Equal(
            HttpStatusCode.OK,
            await PostAsync(
                ["--TeamSpeak:Http:AllowedOrigins:0=https://admin.example.com", "--TeamSpeak:Http:AllowedHosts:0=teamspeak6-mcp"],
                request =>
                {
                    request.Headers.TryAddWithoutValidation("Origin", "https://admin.example.com");
                    request.Headers.Host = "teamspeak6-mcp";
                }));
    }

    [Fact]
    public async Task Requires_the_bearer_token_once_one_is_configured()
    {
        string[] args = [$"--TeamSpeak:Http:BearerToken={Token}"];

        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(args));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            await PostAsync(args, request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer wrong")));
        Assert.Equal(
            HttpStatusCode.OK,
            await PostAsync(args, request => request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}")));
    }

    [Fact]
    public async Task With_a_token_a_foreign_origin_is_still_refused()
    {
        Assert.Equal(
            HttpStatusCode.Forbidden,
            await PostAsync(
                [$"--TeamSpeak:Http:BearerToken={Token}"],
                request =>
                {
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
                    request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
                }));
    }

    [Fact]
    public void Refuses_to_start_reachable_from_other_machines_without_a_token()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => McpHostFactory.CreateHttpApp([], "http://0.0.0.0:0"));

        Assert.Contains("TSMCP_TeamSpeak__Http__BearerToken", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starts_reachable_from_other_machines_with_a_token()
    {
        await using var app = McpHostFactory.CreateHttpApp([$"--TeamSpeak:Http:BearerToken={Token}"], "http://0.0.0.0:0");

        Assert.NotNull(app);
    }

    [Fact]
    public void Refuses_a_token_too_short_to_be_safe()
    {
        Assert.Throws<InvalidOperationException>(() =>
            McpHostFactory.CreateHttpApp(["--TeamSpeak:Http:BearerToken=short"], "http://127.0.0.1:0"));
    }
}