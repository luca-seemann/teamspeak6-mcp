using System.Text.Json;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

/// <summary>Results keep text as it is, escaping only what JSON cannot hold.</summary>
public class ResultJsonTests
{
    private sealed record Channel(string Name);

    private static string Serialize(string name) => JsonSerializer.Serialize(new Channel(name), ResultJson.Options);

    [Theory]
    [InlineData("🔒 Admin 🔒")]
    [InlineData("Größe über 500 MB")]
    [InlineData("⛏️ Minecraft")]
    [InlineData("<script>&'")]
    public void Text_outside_ascii_is_written_literally(string name) =>
        Assert.Equal($$"""{"name":"{{name}}"}""", Serialize(name));

    [Theory]
    [InlineData("say \"hi\"", "say \\\"hi\\\"")]
    [InlineData("C:\\temp", "C:\\\\temp")]
    [InlineData("two\nlines\tand\r", "two\\nlines\\tand\\r")]
    [InlineData("bell\u0007", "bell\\u0007")]
    public void What_json_cannot_hold_is_escaped(string name, string expected) =>
        Assert.Equal($$"""{"name":"{{expected}}"}""", Serialize(name));

    [Fact]
    public void An_unpaired_surrogate_becomes_the_replacement_character() =>
        Assert.Equal("{\"name\":\"lone � surrogate\"}", Serialize("lone \uD83D surrogate"));

    [Theory]
    [InlineData("🔒 \"quoted\" \\ 💤\n\u0001 Größe")]
    [InlineData("")]
    public void Everything_written_reads_back_the_same(string name) =>
        Assert.Equal(name, JsonSerializer.Deserialize<Channel>(Serialize(name), ResultJson.Options)!.Name);
}