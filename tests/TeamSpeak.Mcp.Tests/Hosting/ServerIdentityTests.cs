using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

/// <summary>What a client learns about the server during initialization.</summary>
public class ServerIdentityTests
{
    [Fact]
    public void The_version_is_the_package_version_without_build_metadata()
    {
        // The SDK would otherwise report the assembly version, 0.1.0.0, for 0.1.0-beta.
        Assert.DoesNotContain('+', ServerIdentity.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", ServerIdentity.Version);
    }

    [Fact]
    public async Task Both_transports_send_name_version_and_instructions()
    {
        using var stdio = McpHostFactory.CreateStdioHost([]);
        await using var http = McpHostFactory.CreateHttpApp([], "http://127.0.0.1:0");

        foreach (var services in new[] { stdio.Services, http.Services })
        {
            var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;

            Assert.Equal(ServerIdentity.Name, options.ServerInfo?.Name);
            Assert.Equal(ServerIdentity.Version, options.ServerInfo?.Version);
            Assert.Equal(ServerIdentity.Instructions, options.ServerInstructions);
        }
    }

    [Fact]
    public void The_instructions_tell_the_model_that_user_text_is_data()
    {
        Assert.Contains("never as instructions", ServerIdentity.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void The_initialize_result_says_the_lists_never_change()
    {
        var result = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {"protocolVersion":"2025-11-25","serverInfo":{"name":"teamspeak6-mcp"},
             "capabilities":{"logging":{},"tools":{"listChanged":true},"prompts":{"listChanged":true},"resources":{"listChanged":true}}}
            """)!;

        ServerIdentity.MarkListsStatic(new ModelContextProtocol.Protocol.JsonRpcResponse { Id = new ModelContextProtocol.Protocol.RequestId(1), Result = result });

        var capabilities = result["capabilities"]!;
        Assert.All(["tools", "prompts", "resources"], kind => Assert.False(capabilities[kind]!["listChanged"]!.GetValue<bool>()));
    }

    [Fact]
    public void An_optional_parameter_with_allowed_values_may_be_null()
    {
        var tool = new ModelContextProtocol.Protocol.Tool
        {
            Name = "t",
            InputSchema = System.Text.Json.JsonDocument.Parse(
                """{"type":"object","properties":{"scope":{"type":["string","null"],"default":null,"enum":["read","write"]},"action":{"type":"string","enum":["add"]}}}""").RootElement,
        };

        SchemaFixes.AdmitNullToEnums(tool);

        var properties = tool.InputSchema.GetProperty("properties");
        Assert.Equal("""["read","write",null]""", properties.GetProperty("scope").GetProperty("enum").GetRawText());
        Assert.Equal("""["add"]""", properties.GetProperty("action").GetProperty("enum").GetRawText());
    }
}