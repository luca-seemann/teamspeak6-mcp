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

    /// <summary>Normalises a choice argument and rejects anything outside the allowed values.</summary>
    public static string Choice(string? value, string name, params string[] allowed)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && allowed.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : throw new McpException($"'{name}' must be one of: {string.Join(", ", allowed)}.");
    }

    /// <summary>
    /// Checks that every property belongs to the object being changed, and copies them into a
    /// parameter set.
    /// </summary>
    /// <remarks>
    /// The prefix check keeps a properties map from carrying parameters that change what a command
    /// addresses, such as a <c>sid</c> or <c>cid</c> the tool sets itself.
    /// </remarks>
    public static Dictionary<string, string> Properties(
        IReadOnlyDictionary<string, string>? properties,
        string prefix,
        bool required)
    {
        if (properties is null || properties.Count == 0)
        {
            return required
                ? throw new McpException($"Name at least one {prefix}* property to change.")
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (var (key, value) in properties)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new McpException(
                    $"'{key}' is not a {prefix}* property. Use the ServerQuery property names, as the matching info tool shows them.");
            }

            if (string.IsNullOrEmpty(value))
            {
                throw new McpException(
                    $"'{key}' has no value. TeamSpeak treats an empty value as a missing parameter, so a property cannot be cleared this way.");
            }
        }

        return new Dictionary<string, string>(properties, StringComparer.Ordinal);
    }

    /// <summary>
    /// Refuses an irreversible action unless the caller repeated the name of what it destroys.
    /// </summary>
    public static void RequireConfirmation(string? given, string expected, string what)
    {
        // Without a name to compare against, nothing could be confirmed, not even an empty answer.
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new McpException($"Nothing was changed: the {what}'s name could not be read, so the action cannot be confirmed.");
        }

        if (!string.Equals(given?.Trim(), expected.Trim(), StringComparison.Ordinal))
        {
            throw new McpException(
                $"Nothing was changed. To confirm, pass confirmName exactly as the {what} is named: '{expected}'.");
        }
    }

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