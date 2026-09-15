using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;
using TeamSpeak.Mcp.Prompts;

namespace TeamSpeak.Mcp.Tests.Prompts;

public partial class AdminPromptsTests
{
    [GeneratedRegex(@"\bts_[a-z_]+\b")]
    private static partial Regex ToolName();

    private static (List<McpServerPrompt> Prompts, List<McpServerTool> Tools) Surface()
    {
        using var host = McpHostFactory.CreateStdioHost([]);

        return (host.Services.GetServices<McpServerPrompt>().ToList(), host.Services.GetServices<McpServerTool>().ToList());
    }

    /// <summary>Every prompt, rendered with arguments that exercise all of its optional parts.</summary>
    public static TheoryData<string, string, bool> Rendered() => new()
    {
        { "server-audit", AdminPrompts.ServerAudit("prod", "2"), true },
        { "explain-user-permissions", AdminPrompts.ExplainUserPermissions("Alice", "talk", "Lobby", "prod", "2"), true },
        { "cleanup-channel-tree", AdminPrompts.CleanupChannelTree("remove old game channels", "prod", "2"), false },
        { "onboard-new-member", AdminPrompts.OnboardNewMember("Alice", "Member", "Games", "prod", "2"), false },
    };

    [Fact]
    public void Serves_the_four_prompts_with_titles_and_descriptions()
    {
        var prompts = Surface().Prompts.Select(prompt => prompt.ProtocolPrompt).ToList();

        Assert.Equal(
            ["cleanup-channel-tree", "explain-user-permissions", "onboard-new-member", "server-audit"],
            prompts.Select(prompt => prompt.Name).Order(StringComparer.Ordinal));

        Assert.All(prompts, prompt =>
        {
            Assert.False(string.IsNullOrWhiteSpace(prompt.Title), prompt.Name);
            Assert.False(string.IsNullOrWhiteSpace(prompt.Description), prompt.Name);
            Assert.All(prompt.Arguments ?? [], argument => Assert.False(string.IsNullOrWhiteSpace(argument.Description), $"{prompt.Name}.{argument.Name}"));
        });
    }

    [Fact]
    public void Marks_only_the_people_and_actions_as_required_arguments()
    {
        var required = Surface().Prompts
            .SelectMany(prompt => (prompt.ProtocolPrompt.Arguments ?? []).Where(argument => argument.Required == true).Select(argument => $"{prompt.ProtocolPrompt.Name}.{argument.Name}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(["explain-user-permissions.action", "explain-user-permissions.client", "onboard-new-member.member"], required);
    }

    [Theory]
    [MemberData(nameof(Rendered))]
    public void Names_only_tools_that_exist(string prompt, string text, bool readOnly)
    {
        _ = readOnly;
        var tools = Surface().Tools.Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);

        var unknown = ToolName().Matches(text).Select(match => match.Value).Where(name => !tools.Contains(name)).Distinct().ToList();

        Assert.True(unknown.Count == 0, $"{prompt} names tools that do not exist: {string.Join(", ", unknown)}");
    }

    [Theory]
    [MemberData(nameof(Rendered))]
    public void A_read_only_prompt_tells_the_model_to_call_only_reading_tools(string prompt, string text, bool readOnly)
    {
        if (!readOnly)
        {
            return;
        }

        var changing = Surface().Tools
            .Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint != true)
            .Select(tool => tool.ProtocolTool.Name)
            .ToHashSet(StringComparer.Ordinal);

        // A read-only prompt may name a changing tool only as a suggestion, in the closing advice.
        var steps = text[..text.IndexOf("\n\n", text.IndexOf("1. ", StringComparison.Ordinal), StringComparison.Ordinal)];
        var called = ToolName().Matches(steps).Select(match => match.Value).Where(changing.Contains).Distinct().ToList();

        Assert.True(called.Count == 0, $"{prompt} tells the model to call changing tools: {string.Join(", ", called)}");
        Assert.Contains("read-only", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Carries_the_profile_and_virtual_server_into_the_instructions()
    {
        var text = AdminPrompts.ServerAudit("prod", "2");

        Assert.Contains("virtual server 2 of the TeamSpeak profile 'prod'", text, StringComparison.Ordinal);
        Assert.Contains("Pass profile 'prod' and virtualServerId 2 to every tool call", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_profile_asks_the_model_to_check_which_profiles_exist()
    {
        var text = AdminPrompts.CleanupChannelTree();

        Assert.Contains("the default virtual server", text, StringComparison.Ordinal);
        Assert.Contains("ts_profiles_list", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("two")]
    public void Refuses_a_virtual_server_id_that_is_not_a_positive_number(string id) =>
        Assert.Throws<McpException>(() => AdminPrompts.ServerAudit(virtualServerId: id));

    [Fact]
    public void Refuses_to_explain_permissions_without_a_person() =>
        Assert.Throws<McpException>(() => AdminPrompts.ExplainUserPermissions(" ", "talk"));
}