using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>
/// Pins the MCP surface: tool names, descriptions, input and output schemas, and annotations.
/// </summary>
/// <remarks>
/// A model sees nothing but these schemas, so an accidental change to one — a renamed parameter, a
/// lost annotation — is a breaking change for every client. When a change is intended, run the tests
/// with <c>TSMCP_UPDATE_SNAPSHOTS=1</c>, review the diff of <c>Snapshots/tools.json</c> and commit it.
/// </remarks>
public class ToolSchemaSnapshotTests
{
    private const string UpdateVariable = "TSMCP_UPDATE_SNAPSHOTS";

    private static readonly JsonSerializerOptions SnapshotOptions =
        new(McpJsonUtilities.DefaultOptions) { WriteIndented = true };

    private static List<Tool> Tools()
    {
        using var host = McpHostFactory.CreateStdioHost([]);

        return host.Services.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string SnapshotPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots", "tools.json");

    [Fact]
    public void Tool_schemas_match_the_committed_snapshot()
    {
        var actual = JsonSerializer
            .Serialize(Tools(), SnapshotOptions)
            .ReplaceLineEndings("\n");

        var path = SnapshotPath();
        var update = Environment.GetEnvironmentVariable(UpdateVariable) == "1";

        if (update || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);

            if (!update)
            {
                Assert.Fail($"No snapshot existed, so one was written to {path}. Review it, commit it, and run the tests again.");
            }

            return;
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void Serves_the_ten_phase_four_tools()
    {
        Assert.Equal(
            [
                "ts_channel_info", "ts_channel_list", "ts_client_info", "ts_client_list", "ts_instance_info",
                "ts_profiles_list", "ts_query_raw", "ts_vserver_info", "ts_vserver_list", "ts_whoami",
            ],
            Tools().Select(tool => tool.Name));
    }

    [Fact]
    public void Every_tool_has_a_title_a_description_and_structured_output()
    {
        Assert.All(Tools(), tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Annotations?.Title), tool.Name);
            Assert.NotNull(tool.OutputSchema);
        });
    }

    [Fact]
    public void Annotates_reading_tools_as_read_only_and_the_raw_query_as_destructive()
    {
        Assert.All(Tools(), tool =>
        {
            var isRaw = tool.Name == "ts_query_raw";

            Assert.Equal(!isRaw, tool.Annotations?.ReadOnlyHint);
            Assert.Equal(isRaw, tool.Annotations?.DestructiveHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
        });
    }

    [Fact]
    public void Every_server_facing_tool_accepts_a_profile_and_none_exposes_a_cancellation_token()
    {
        Assert.All(Tools(), tool =>
        {
            var properties = tool.InputSchema.TryGetProperty("properties", out var found)
                ? found.EnumerateObject().Select(property => property.Name).ToList()
                : [];

            if (tool.Name != "ts_profiles_list")
            {
                Assert.Contains("profile", properties);
            }

            Assert.DoesNotContain("cancellationToken", properties);
        });
    }
}