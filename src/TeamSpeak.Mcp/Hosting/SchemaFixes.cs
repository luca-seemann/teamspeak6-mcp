using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Corrects what the SDK's schema generation gets wrong for these tools.
/// </summary>
public static class SchemaFixes
{
    /// <summary>
    /// Lets an optional parameter with allowed values be <see langword="null"/>, as its type says.
    /// </summary>
    /// <param name="tool">The tool whose input schema to correct.</param>
    /// <remarks>
    /// An optional <c>[AllowedValues("read", "write")] string? scope = null</c> becomes
    /// <c>{"type": ["string", "null"], "default": null, "enum": ["read", "write"]}</c>: the SDK drops a
    /// <see langword="null"/> from the allowed values, so the schema rejects its own default. A client
    /// that validates arguments would refuse to send the parameter as null.
    /// </remarks>
    public static void AdmitNullToEnums(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (JsonNode.Parse(tool.InputSchema.GetRawText()) is not JsonObject schema
            || schema["properties"] is not JsonObject properties)
        {
            return;
        }

        var changed = false;
        foreach (var (_, node) in properties)
        {
            if (node is JsonObject property
                && property["enum"] is JsonArray allowed
                && property["type"] is JsonArray types
                && types.Any(type => type?.GetValue<string>() == "null")
                && !allowed.Any(value => value is null))
            {
                allowed.Add(null);
                changed = true;
            }
        }

        if (changed)
        {
            tool.InputSchema = JsonSerializer.SerializeToElement(schema);
        }
    }
}