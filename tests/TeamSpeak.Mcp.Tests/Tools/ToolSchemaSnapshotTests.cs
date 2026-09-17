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

        // The collection the server actually serves, after tool groups and schema corrections.
        return host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value.ToolCollection!
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

    /// <summary>Tools annotated as destructive: every one that needs Destructive for at least one action.</summary>
    private static readonly string[] DestructiveTools =
    [
        "ts_query_raw", "ts_vserver_create", "ts_vserver_power", "ts_vserver_delete", "ts_vserver_snapshot_deploy", "ts_instance_edit",
        "ts_temp_password", "ts_channel_delete", "ts_client_kick", "ts_clientdb_delete", "ts_servergroup_delete",
        "ts_channelgroup_delete", "ts_perm_reset", "ts_ban_add", "ts_token_manage", "ts_apikey_manage",
        "ts_querylogin_manage", "ts_file_upload", "ts_file_delete", "ts_file_manage",
        "ts_perm_set", "ts_servergroup_membership", "ts_vserver_edit",
    ];

    /// <summary>Tools that change something but never need more than Write.</summary>
    private static readonly string[] WriteTools =
    [
        "ts_vserver_snapshot_create", "ts_channel_create", "ts_channel_edit",
        "ts_channel_move", "ts_client_move", "ts_client_poke", "ts_client_edit", "ts_message_send", "ts_offline_message",
        "ts_servergroup_manage", "ts_channelgroup_manage", "ts_client_channelgroup_set",
        "ts_ban_delete", "ts_complaint_delete", "ts_custom_property", "ts_log_add",

        // Needs only ReadOnly on the server, but can write a file on this machine.
        "ts_file_download",
    ];

    [Fact]
    public void Annotates_every_changing_tool_by_the_worst_it_can_do()
    {
        Assert.All(Tools(), tool =>
        {
            var destructive = DestructiveTools.Contains(tool.Name);
            var changes = destructive || WriteTools.Contains(tool.Name);

            Assert.Equal(!changes, tool.Annotations?.ReadOnlyHint);
            Assert.Equal(destructive, tool.Annotations?.DestructiveHint);
        });
    }

    [Fact]
    public void Serves_the_complete_read_and_write_surface()
    {
        var reads = new[]
        {
                "ts_apikey_list", "ts_ban_list", "ts_channel_find", "ts_channel_info", "ts_channel_list",
                "ts_channelgroup_list", "ts_channelgroup_members", "ts_client_find", "ts_client_groups",
                "ts_client_info", "ts_client_list", "ts_client_resolve", "ts_clientdb_find", "ts_clientdb_info",
                "ts_clientdb_list", "ts_command_help", "ts_complaint_list", "ts_custom_info", "ts_custom_search",
                "ts_events_poll", "ts_events_status", "ts_events_subscribe", "ts_events_unsubscribe", "ts_events_wait",
                "ts_file_info", "ts_file_list", "ts_file_transfers",
                "ts_health_report",
                "ts_instance_info", "ts_log_view", "ts_message_get", "ts_message_list", "ts_perm_assigned",
                "ts_perm_effective", "ts_perm_find", "ts_perm_list", "ts_profiles_list",
                "ts_querylogin_list", "ts_servergroup_list", "ts_servergroup_members", "ts_token_list",
                "ts_vserver_info", "ts_vserver_list", "ts_whoami",
        };

        Assert.Equal(
            reads.Concat(WriteTools).Concat(DestructiveTools).Distinct().Order(StringComparer.Ordinal),
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
    public void No_tool_claims_to_reach_beyond_the_configured_servers() =>
        Assert.All(Tools(), tool => Assert.False(tool.Annotations?.OpenWorldHint, tool.Name));

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