using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

/// <summary>
/// What a client actually receives from a tool call, through the whole server pipeline.
/// </summary>
/// <remarks>
/// The TOON mode once passed every unit test while sending JSON: without an output schema the SDK
/// creates no structured content, which the filter was reading. Only a real call shows that.
/// <c>ts_profiles_list</c> needs no TeamSpeak server.
/// </remarks>
public class ToolResultPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] TwoProfiles =
    [
        "--TeamSpeak:Profiles:alpha:Host=alpha.example.com", "--TeamSpeak:Profiles:alpha:Password=x",
        "--TeamSpeak:Profiles:beta:Host=beta.example.com", "--TeamSpeak:Profiles:beta:Password=x",
    ];

    /// <summary>Starts the HTTP app, lists the tools and calls ts_profiles_list.</summary>
    private static async Task<(JsonArray Tools, JsonObject Result)> CallProfilesListAsync(params string[] extraArgs)
    {
        await using var app = McpHostFactory.CreateHttpApp([.. TwoProfiles, .. extraArgs], "http://127.0.0.1:0");
        await app.StartAsync(Ct);

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
            using var client = new HttpClient();
            string? session = null;

            async Task<JsonObject?> SendAsync(object message)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, address)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(message), Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
                if (session is not null)
                {
                    request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
                }

                using var response = await client.SendAsync(request, Ct);
                response.EnsureSuccessStatusCode();
                if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
                {
                    session = ids.First();
                }

                var body = await response.Content.ReadAsStringAsync(Ct);
                var json = body.TrimStart().StartsWith('{')
                    ? body
                    : body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].Trim()).LastOrDefault();

                return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json)!.AsObject();
            }

            await SendAsync(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } });
            await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
            var tools = (await SendAsync(new { jsonrpc = "2.0", id = 2, method = "tools/list" }))!["result"]!["tools"]!.AsArray();
            var call = (await SendAsync(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "ts_profiles_list", arguments = new { } } }))!;

            return (tools, call["result"]!.AsObject());
        }
        finally
        {
            await app.StopAsync(Ct);
        }
    }

    private static JsonObject Tool(JsonArray tools, string name) =>
        tools.Select(tool => tool!.AsObject()).Single(tool => (string?)tool["name"] == name);

    private static string Text(JsonObject result) => (string)result["content"]!.AsArray().Single()!["text"]!;

    [Fact]
    public async Task By_default_a_client_gets_structured_content_its_schema_and_the_same_as_json_text()
    {
        var (tools, result) = await CallProfilesListAsync();

        Assert.NotNull(Tool(tools, "ts_profiles_list")["outputSchema"]);
        Assert.NotNull(result["structuredContent"]);
        Assert.Equal("alpha", (string?)JsonNode.Parse(Text(result))!["profiles"]![0]!["name"]);
    }

    [Fact]
    public async Task With_toon_a_client_gets_toon_text_only()
    {
        var (tools, result) = await CallProfilesListAsync("--TeamSpeak:ToolResultText=Toon");

        Assert.Null(Tool(tools, "ts_profiles_list")["outputSchema"]);
        Assert.Null(result["structuredContent"]);
        Assert.StartsWith("profiles[2]{name,host,", Text(result), StringComparison.Ordinal);
        Assert.Contains("alpha,alpha.example.com,", Text(result), StringComparison.Ordinal);
    }
}