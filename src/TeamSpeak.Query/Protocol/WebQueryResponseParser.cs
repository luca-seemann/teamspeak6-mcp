using System.Text.Json;

namespace TeamSpeak.Query.Protocol;

/// <summary>
/// Parses responses from the HTTP/HTTPS WebQuery interface into the same shape as the SSH parser.
/// </summary>
/// <remarks>
/// <para>
/// The WebQuery envelope is <c>{"body":[…],"status":{"code":0,"message":"ok"}}</c>. Normalising it
/// onto <see cref="QueryResponse"/> is what lets every tool work over either transport.
/// </para>
/// <para>
/// Three differences from the line-based interface have to be smoothed over here. Values are never
/// escaped, because JSON does its own quoting. Every value arrives as a JSON string, even numeric
/// ones such as <c>"build":"1785239375"</c>. And an empty result set omits <c>body</c> entirely
/// rather than sending an empty array, which is why a missing <c>body</c> is not an error.
/// </para>
/// </remarks>
public static class WebQueryResponseParser
{
    /// <summary>Parses a WebQuery JSON response.</summary>
    /// <param name="json">The raw response body.</param>
    /// <returns>The decoded response.</returns>
    /// <exception cref="QueryProtocolException">
    /// Thrown when the payload is not valid JSON or carries no <c>status</c> object.
    /// </exception>
    public static QueryResponse Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new QueryProtocolException("The WebQuery response was not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("status", out var status))
            {
                throw new QueryProtocolException("The WebQuery response carried no 'status' object.");
            }

            var records = new List<IReadOnlyDictionary<string, string>>();

            // 'body' is absent, not empty, when there are no results.
            if (root.TryGetProperty("body", out var body) && body.ValueKind is JsonValueKind.Array)
            {
                foreach (var element in body.EnumerateArray())
                {
                    records.Add(ParseRecord(element));
                }
            }

            return new QueryResponse(records, ParseStatus(status));
        }
    }

    private static QueryError ParseStatus(JsonElement status)
    {
        var id = status.TryGetProperty("code", out var code) && code.TryGetInt32(out var parsed)
            ? parsed
            : -1;

        var message = status.TryGetProperty("message", out var m) ? m.GetString() : null;
        var extra = status.TryGetProperty("extra_message", out var e) ? e.GetString() : null;

        return new QueryError(id, message ?? string.Empty, extra);
    }

    private static Dictionary<string, string> ParseRecord(JsonElement element)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        if (element.ValueKind is not JsonValueKind.Object)
        {
            return fields;
        }

        foreach (var property in element.EnumerateObject())
        {
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => property.Value.GetRawText(),
            };
        }

        return fields;
    }
}