using System.Text.Json;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Says in words which argument of a tool call does not fit the tool's input schema.
/// </summary>
/// <remarks>
/// The SDK binds arguments before a tool runs. When one is missing or of the wrong type, it throws an
/// exception that names neither the argument nor the tool, and the model is only told that an error
/// occurred. Comparing the arguments with the schema afterwards recovers what went wrong. Only type,
/// <c>enum</c> and <c>required</c> are checked, the parts of JSON Schema the tool schemas use.
/// </remarks>
public static class ArgumentMismatch
{
    /// <summary>Finds the first argument that does not fit the schema.</summary>
    /// <param name="inputSchema">The tool's input schema.</param>
    /// <param name="arguments">The arguments the client sent; <see langword="null"/> when it sent none.</param>
    /// <returns>A sentence naming the argument and what it has to be; <see langword="null"/> when every argument fits.</returns>
    public static string? Describe(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        arguments ??= new Dictionary<string, JsonElement>();

        if (inputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(entry => entry.GetString()).OfType<string>())
            {
                if (!arguments.TryGetValue(name, out var given) || given.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return $"'{name}' is required.";
                }
            }
        }

        if (!inputSchema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var (name, value) in arguments)
        {
            if (properties.TryGetProperty(name, out var property) && Check(name, property, value) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? Check(string name, JsonElement schema, JsonElement value)
    {
        var types = Types(schema);
        if (types.Count > 0 && !types.Any(type => Fits(type, value)))
        {
            return $"'{name}' must be {string.Join(" or ", types.Select(Article))}, not {Shown(value)}.";
        }

        if (value.ValueKind != JsonValueKind.Null && schema.TryGetProperty("enum", out var allowed) && !Contains(allowed, value))
        {
            return $"'{name}' must be one of: {Listed(allowed)}; {Shown(value)} is not.";
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            foreach (var item in value.EnumerateArray())
            {
                if (Check($"{name}[]", items, item) is { } problem)
                {
                    return problem;
                }
            }
        }

        return null;
    }

    private static List<string> Types(JsonElement schema) =>
        !schema.TryGetProperty("type", out var type)
            ? []
            : type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(entry => entry.GetString()).OfType<string>().ToList()
                : type.GetString() is { } single ? [single] : [];

    private static bool Fits(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool Contains(JsonElement allowed, JsonElement value) =>
        allowed.ValueKind != JsonValueKind.Array
        || allowed.EnumerateArray().Any(entry => JsonElement.DeepEquals(entry, value));

    private static string Listed(JsonElement allowed) =>
        string.Join(", ", allowed.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.GetRawText()));

    private static string Article(string type) => type switch
    {
        "integer" => "an integer",
        "array" => "an array",
        "object" => "an object",
        "null" => "null",
        _ => $"a {type}",
    };

    private static string Shown(JsonElement value)
    {
        var raw = value.GetRawText();
        if (raw.Length > 40)
        {
            raw = string.Concat(raw.AsSpan(0, 37), "...");
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => $"the string {raw}",
            JsonValueKind.Number => $"the number {raw}",
            JsonValueKind.Array => $"the array {raw}",
            JsonValueKind.Object => $"the object {raw}",
            _ => raw,
        };
    }
}