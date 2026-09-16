using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

/// <summary>Writing the text block of tool results as TOON, and only where it pays.</summary>
public class ToonResultTextTests
{
    /// <summary>A result as the SDK builds it with the server's options: structured content, and the same as JSON text.</summary>
    private static CallToolResult Result(string json, bool isError = false) => new()
    {
        IsError = isError ? true : null,
        StructuredContent = JsonDocument.Parse(json).RootElement,
        Content = [new TextContentBlock { Text = JsonNode.Parse(json)!.ToJsonString(ResultJson.Options) }],
    };


    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private const string Table =
        """
        {"target":"server group 6","totalMatches":3,"permissions":[
          {"id":26,"name":"b_virtualserver_info_view","value":1,"negated":false,"skip":false},
          {"id":89,"name":"i_channel_needed_join_power","value":75,"negated":false,"skip":true},
          {"id":226,"name":"i_client_talk_power","value":75,"negated":true,"skip":false}]}
        """;

    [Fact]
    public void A_list_of_uniform_records_becomes_a_shorter_table_and_the_only_copy_of_the_data()
    {
        var result = Result(Table);
        var json = Text(result);

        ToonResultText.Rewrite(result);

        var toon = Text(result);
        Assert.True(toon.Length < json.Length);
        Assert.Contains("permissions[3]{id,name,value,negated,skip}:", toon, StringComparison.Ordinal);
        Assert.Contains("226,i_client_talk_power,75,true,false", toon, StringComparison.Ordinal);

        // Claude Code shows the model the structured content whenever there is some, so it has to go.
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public void Without_an_output_schema_the_sdk_sends_only_json_text_and_that_is_encoded()
    {
        // The shape a live call has in this mode: the SDK creates no structured content without an output schema.
        var result = new CallToolResult { Content = [new TextContentBlock { Text = JsonNode.Parse(Table)!.ToJsonString(ResultJson.Options) }] };

        ToonResultText.Rewrite(result);

        Assert.Contains("permissions[3]{id,name,value,negated,skip}:", Text(result), StringComparison.Ordinal);
        Assert.Null(result.StructuredContent);
    }
    [Fact]
    public void A_result_toon_would_not_shorten_stays_json()
    {
        // Nested records of differing shape, as in a channel tree, gain nothing from TOON.
        var result = Result("""{"a":{"b":{"c":{"d":1}}}}""");
        var json = Text(result);

        ToonResultText.Rewrite(result);

        Assert.Equal(json, Text(result));
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public void Errors_and_plain_text_are_left_alone()
    {
        var error = Result(Table, isError: true);
        var errorText = Text(error);
        var plain = new CallToolResult { Content = [new TextContentBlock { Text = "Deleted API key 36." }] };

        ToonResultText.Rewrite(error);
        ToonResultText.Rewrite(plain);

        Assert.Equal(errorText, Text(error));
        Assert.Equal("Deleted API key 36.", Text(plain));
    }

    [Theory]
    [InlineData("Talk, Chill & Code")]
    [InlineData("key: value")]
    [InlineData("say \"hi\"")]
    [InlineData("two\nlines")]
    [InlineData("Größe über 500 MB")]
    [InlineData("  padded  ")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("")]
    public void Text_written_by_users_survives_as_one_unambiguous_value(string name)
    {
        var channels = new JsonArray(
            new JsonObject { ["id"] = 1, ["name"] = name, ["topic"] = "x" },
            new JsonObject { ["id"] = 2, ["name"] = "Lobby", ["topic"] = "y" },
            new JsonObject { ["id"] = 3, ["name"] = "AFK", ["topic"] = "z" });
        var json = new JsonObject { ["channels"] = channels }.ToJsonString(ResultJson.Options);
        var result = Result(json);

        ToonResultText.Rewrite(result);

        var row = Text(result).Split('\n').Single(line => line.TrimStart().StartsWith("1,", StringComparison.Ordinal));
        var value = row.TrimStart()[2..^2];

        // A value that could be read as a delimiter, a number, a boolean or padding must be a JSON string.
        if (name.Length == 0 || name.IndexOfAny([',', ':', '"', '\n']) >= 0 || name.Trim() != name || name is "42" or "true")
        {
            Assert.Equal(name, JsonSerializer.Deserialize<string>(value));
        }
        else
        {
            Assert.Equal(name, value);
        }
    }

    [Fact]
    public void Json_is_the_default_and_an_unknown_format_stops_the_start()
    {
        using var host = McpHostFactory.CreateStdioHost([]);
        Assert.DoesNotContain("TOON", host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInstructions, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidOperationException>(() => McpHostFactory.CreateStdioHost(["--TeamSpeak:ToolResultText=yaml"]));
        Assert.Contains("Json or Toon", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Toon")]
    [InlineData("toon")]
    public void With_toon_the_instructions_explain_the_format(string value)
    {
        using var host = McpHostFactory.CreateStdioHost([$"--TeamSpeak:ToolResultText={value}"]);

        var instructions = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInstructions;

        Assert.StartsWith(ServerIdentity.Instructions.TrimEnd(), instructions, StringComparison.Ordinal);
        Assert.EndsWith(ToonResultText.InstructionsNote, instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void With_toon_no_tool_declares_an_output_schema_and_with_json_every_tool_does()
    {
        using var json = McpHostFactory.CreateStdioHost([]);
        using var toon = McpHostFactory.CreateStdioHost(["--TeamSpeak:ToolResultText=Toon"]);

        static IEnumerable<ModelContextProtocol.Protocol.Tool> Tools(Microsoft.Extensions.Hosting.IHost host) =>
            host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!.Select(tool => tool.ProtocolTool);

        Assert.All(Tools(json), tool => Assert.NotNull(tool.OutputSchema));
        Assert.All(Tools(toon), tool => Assert.Null(tool.OutputSchema));
    }
}