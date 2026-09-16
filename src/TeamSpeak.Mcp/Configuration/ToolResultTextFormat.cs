namespace TeamSpeak.Mcp.Configuration;

/// <summary>
/// What a tool result carries, set by <c>TeamSpeak:ToolResultText</c>.
/// </summary>
public enum ToolResultTextFormat
{
    /// <summary>
    /// Structured content matching each tool's output schema, and the same data as JSON text, as the MCP
    /// specification suggests. For clients that read typed results.
    /// </summary>
    Json,

    /// <summary>
    /// Text only: no output schemas and no structured content, and the text written as TOON wherever that
    /// is shorter than JSON. For clients such as Claude Code that give their model the structured
    /// content whenever there is some.
    /// </summary>
    Toon,
}