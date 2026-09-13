namespace TeamSpeak.Query.Protocol;

/// <summary>
/// Parses raw responses from the line-based (SSH) ServerQuery interface.
/// </summary>
/// <remarks>
/// A response is zero or more payload lines followed by a status line such as
/// <c>error id=0 msg=ok</c>. There is no prompt to synchronise on — despite what the official
/// documentation shows, the server never emits one — so the status line is the only frame marker.
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

        foreach (var line in EnumerateLines(raw))
        {
            if (line.StartsWith(StatusPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses a complete raw response.</summary>
    /// <param name="raw">The raw text, including the terminating status line.</param>
    /// <returns>The decoded response.</returns>
    /// <exception cref="QueryProtocolException">Thrown when no status line is present.</exception>
    public static QueryResponse Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var records = new List<IReadOnlyDictionary<string, string>>();
        QueryError? error = null;

        foreach (var line in EnumerateLines(raw))
        {
            if (line.StartsWith(StatusPrefix, StringComparison.Ordinal))
            {
                error = ParseStatus(line);
                break;
            }

            // A payload line holds one or more records separated by '|'.
            foreach (var record in line.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                records.Add(ParseRecord(record));
            }
        }

        return error is null
            ? throw new QueryProtocolException("The response contained no 'error id=' status line.")
            : new QueryResponse(records, error);
    }

    private static IEnumerable<string> EnumerateLines(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ');
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
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