namespace TeamSpeak.Query.Protocol;

/// <summary>
/// Parses raw responses from the line-based (SSH) ServerQuery interface.
/// </summary>
/// <remarks>
/// <para>
/// A response is zero or more payload lines followed by a status line such as
/// <c>error id=0 msg=ok</c>. There is no prompt to synchronise on. Despite what the official
/// documentation shows, the server never emits one, so the status line is the only frame marker.
/// </para>
/// <para>
/// Only a status line that starts its line ends a response. Measured on 6.0.0-beta12.1: a
/// <c>help</c> page quotes example responses indented by two spaces, status line included, and
/// <c>help servernotifyregister</c> quotes two of them before its real status line.
/// </para>
/// </remarks>
public static class QueryResponseParser
{
    private const string StatusPrefix = "error ";

    /// <summary>
    /// Determines whether the accumulated text contains a complete response.
    /// </summary>
    /// <param name="raw">Everything read from the connection so far.</param>
    /// <returns><see langword="true"/> once the terminating status line has arrived.</returns>
    public static bool IsComplete(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return EnumerateLines(raw).Any(IsStatusLine);
    }

    /// <summary>Parses a complete raw response.</summary>
    /// <param name="raw">The raw text, including the terminating status line.</param>
    /// <returns>The decoded response.</returns>
    /// <exception cref="QueryProtocolException">Thrown when no status line is present.</exception>
    public static QueryResponse Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var records = new List<QueryRecord>();
        var text = new List<string>();
        QueryError? error = null;

        foreach (var line in EnumerateLines(raw))
        {
            if (IsStatusLine(line))
            {
                error = ParseStatus(line);
                break;
            }

            text.Add(line);

            var trimmed = line.Trim(' ');
            if (trimmed.Length == 0)
            {
                continue;
            }

            // A payload line holds one or more records separated by '|'.
            foreach (var record in trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                records.Add(new QueryRecord(ParseRecord(record)));
            }
        }

        return error is null
            ? throw new QueryProtocolException("The response contained no 'error id=' status line.")
            : new QueryResponse(records, error) { Text = string.Join('\n', WithoutBlankEdges(text)) };
    }

    private static bool IsStatusLine(string line) => line.StartsWith(StatusPrefix, StringComparison.Ordinal);

    /// <summary>Splits into lines, removing carriage returns but keeping indentation.</summary>
    /// <remarks>
    /// The server ends lines with <c>\n\r</c>, so every line after the first starts with the carriage
    /// return of the break before it. Spaces are left alone: they are what tells a quoted example
    /// status line from the real one.
    /// </remarks>
    private static IEnumerable<string> EnumerateLines(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            yield return line.Trim('\r');
        }
    }

    private static IEnumerable<string> WithoutBlankEdges(List<string> lines)
    {
        var first = lines.FindIndex(line => line.Trim(' ').Length > 0);
        if (first < 0)
        {
            return [];
        }

        var last = lines.FindLastIndex(line => line.Trim(' ').Length > 0);
        return lines.Skip(first).Take(last - first + 1);
    }

    private static QueryError ParseStatus(string line)
    {
        var fields = ParseRecord(line[StatusPrefix.Length..]);

        var id = fields.TryGetValue("id", out var rawId) && int.TryParse(rawId, out var parsed)
            ? parsed
            : -1;

        fields.TryGetValue("msg", out var message);
        fields.TryGetValue("extra_msg", out var extra);

        return new QueryError(id, message ?? string.Empty, extra);
    }

    private static Dictionary<string, string> ParseRecord(string record)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var token in record.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf('=', StringComparison.Ordinal);

            // An empty value is sent as a bare key with no '=' at all, for example the
            // client_nickname in a serveradmin whoami. Record it as present but empty.
            if (separator < 0)
            {
                fields[QueryEscaping.Unescape(token)] = string.Empty;
            }
            else
            {
                var key = QueryEscaping.Unescape(token[..separator]);
                fields[key] = QueryEscaping.Unescape(token[(separator + 1)..]);
            }
        }

        return fields;
    }
}