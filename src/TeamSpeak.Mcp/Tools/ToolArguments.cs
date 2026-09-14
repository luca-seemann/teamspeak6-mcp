using System.Globalization;

using ModelContextProtocol;

using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>Argument checks and conversions shared by the tools.</summary>
internal static class ToolArguments
{
    /// <summary>Rejects an empty text argument before it reaches the server.</summary>
    /// <remarks>
    /// TeamSpeak 6 answers an empty value with <c>1542 missing required parameter</c>, which reads as
    /// a bug in the tool rather than in the call; saying so up front is clearer.
    /// </remarks>
    public static string RequireText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new McpException(
                $"'{name}' must not be empty; TeamSpeak treats an empty value as a missing parameter.")
            : value.Trim();

    /// <summary>Formats a number for a command parameter.</summary>
    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Turns plain text into a substring match for the commands that take SQL-style patterns.
    /// </summary>
    /// <remarks>A pattern that already contains <c>%</c> is taken as written.</remarks>
    public static string SqlPattern(string value) =>
        value.Contains('%', StringComparison.Ordinal) ? value : $"%{value}%";

    /// <summary>Gets a text field, or <see langword="null"/> when it is absent or empty.</summary>
    public static string? Optional(QueryRecord record, string key) =>
        record.GetString(key) is { Length: > 0 } value ? value : null;

    /// <summary>Copies the first record's fields, or nothing when there is no record.</summary>
    public static Dictionary<string, string> FirstFields(IReadOnlyList<QueryRecord> records) =>
        records.Count > 0 ? new Dictionary<string, string>(QueryExecutor.ToFields(records[0])) : [];
}