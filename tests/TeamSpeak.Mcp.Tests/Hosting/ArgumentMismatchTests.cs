using System.Text.Json;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

/// <summary>Naming the argument that does not fit, in the shapes the generated tool schemas take.</summary>
public class ArgumentMismatchTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "channelId": { "type": "integer" },
            "action": { "type": "string", "enum": ["start", "stop"] },
            "level": { "type": "string", "default": "info", "enum": ["info", "error"] },
            "reason": { "type": ["string", "null"], "default": null },
            "categories": { "type": ["array", "null"], "items": { "type": "string", "enum": ["server", "bans"] }, "default": null }
          },
          "required": ["channelId", "action"]
        }
        """).RootElement;

    private static string? Describe(string arguments) =>
        ArgumentMismatch.Describe(Schema, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments));

    [Fact]
    public void Arguments_that_fit_raise_nothing() =>
        Assert.Null(Describe("""{"channelId": 5, "action": "stop", "reason": null, "categories": ["bans"]}"""));

    [Theory]
    [InlineData("""{"action": "stop"}""", "'channelId' is required.")]
    [InlineData("""{"channelId": null, "action": "stop"}""", "'channelId' is required.")]
    [InlineData("""{"channelId": "abc", "action": "stop"}""", "'channelId' must be an integer, not the string \"abc\".")]
    [InlineData("""{"channelId": 1.5, "action": "stop"}""", "'channelId' must be an integer, not the number 1.5.")]
    [InlineData("""{"channelId": 1, "action": "reboot"}""", "'action' must be one of: start, stop; the string \"reboot\" is not.")]
    [InlineData("""{"channelId": 1, "action": "stop", "reason": 7}""", "'reason' must be a string or null, not the number 7.")]
    [InlineData("""{"channelId": 1, "action": "stop", "categories": "bans"}""", "'categories' must be an array or null, not the string \"bans\".")]
    [InlineData("""{"channelId": 1, "action": "stop", "categories": ["everything"]}""", "'categories[]' must be one of: server, bans; the string \"everything\" is not.")]
    public void Names_the_argument_and_what_it_has_to_be(string arguments, string expected) =>
        Assert.Equal(expected, Describe(arguments));

    [Fact]
    public void No_arguments_at_all_count_as_missing_ones() =>
        Assert.Equal("'channelId' is required.", ArgumentMismatch.Describe(Schema, null));

    [Fact]
    public void Arguments_the_schema_does_not_know_are_left_alone() =>
        Assert.Null(Describe("""{"channelId": 1, "action": "start", "extra": {"x": 1}}"""));
}