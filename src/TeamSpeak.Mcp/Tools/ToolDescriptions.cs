namespace TeamSpeak.Mcp.Tools;

/// <summary>Parameter descriptions shared by many tools, kept identical on purpose.</summary>
internal static class ToolDescriptions
{
    public const string Profile =
        "The configured TeamSpeak server to use, as listed by ts_profiles_list. " +
        "Omit it when only one profile is configured.";

    public const string VirtualServerId =
        "The virtual server to address, as listed by ts_vserver_list. " +
        "Omit it to use the profile's default virtual server.";
}

/// <summary>A single record's fields, for tools that return one entity in full.</summary>
/// <param name="Fields">Every field the server returned, by its ServerQuery name.</param>
public sealed record RecordResult(IReadOnlyDictionary<string, string> Fields);