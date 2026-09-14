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

    public const string VirtualServerProperties =
        "Properties by their ServerQuery names, as ts_vserver_info shows them, for example " +
        "{\"virtualserver_maxclients\": \"64\", \"virtualserver_welcomemessage\": \"Hi\"}.";

    public const string ChannelProperties =
        "Properties by their ServerQuery names, as ts_channel_info shows them, for example " +
        "{\"channel_topic\": \"Raid night\", \"channel_maxclients\": \"10\", \"channel_flag_permanent\": \"1\"}.";

    public const string InstanceProperties =
        "Properties by their ServerQuery names, as ts_instance_info shows them, for example " +
        "{\"serverinstance_serverquery_flood_commands\": \"20\"}.";

    public const string GroupType =
        "regular (the default), template, or query.";

    public const string ConfirmVirtualServer =
        "The virtual server's exact name, as a safeguard against acting on the wrong one.";
}

/// <summary>The outcome of a change.</summary>
/// <param name="Done">What was done, in words.</param>
/// <param name="Details">The fields the server returned, such as a new id or key; empty when it returned none.</param>
/// <param name="Records">Every record, when the server returned more than one.</param>
public sealed record ActionResult(
    string Done,
    IReadOnlyDictionary<string, string> Details,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Records)
{
    /// <summary>Builds a result from a command's records.</summary>
    /// <param name="done">What was done.</param>
    /// <param name="records">The records the server returned.</param>
    /// <returns>The result.</returns>
    public static ActionResult From(string done, IReadOnlyList<TeamSpeak.Query.Protocol.QueryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return new ActionResult(
            done,
            records.Count > 0 ? QueryExecutor.ToFields(records[0]) : new Dictionary<string, string>(),
            records.Count > 1 ? records.Select(QueryExecutor.ToFields).ToList() : []);
    }
}

/// <summary>A single record's fields, for tools that return one entity in full.</summary>
/// <param name="Fields">Every field the server returned, by its ServerQuery name.</param>
public sealed record RecordResult(IReadOnlyDictionary<string, string> Fields);