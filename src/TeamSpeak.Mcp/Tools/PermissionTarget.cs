using ModelContextProtocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>
/// One thing permissions can be assigned to, with the commands that list, add and remove its
/// assignments.
/// </summary>
/// <param name="Label">The target in words, for example <c>server group 6</c>.</param>
/// <param name="ListCommand">The command listing its assignments.</param>
/// <param name="AddCommand">The command adding an assignment.</param>
/// <param name="DeleteCommand">The command removing an assignment.</param>
/// <param name="Parameters">The parameters that address the target.</param>
/// <param name="SupportsNegate">Whether assignments here take a negate flag.</param>
/// <param name="SupportsSkip">Whether assignments here take a skip flag.</param>
internal sealed record PermissionTarget(
    string Label,
    string ListCommand,
    string AddCommand,
    string DeleteCommand,
    IReadOnlyDictionary<string, string> Parameters,
    bool SupportsNegate,
    bool SupportsSkip)
{
    /// <summary>The ways a target can be given.</summary>
    public const string Choices =
        "Pass exactly one target: serverGroupId, channelGroupId, channelId, databaseId, or channelId together with databaseId.";

    /// <summary>Works out the target from whichever ids were given.</summary>
    /// <exception cref="McpException">Thrown when the combination names no single target.</exception>
    /// <remarks>
    /// The flags follow the reference: only server group assignments take negate, and only server
    /// group and client assignments take skip.
    /// </remarks>
    public static PermissionTarget From(int? serverGroupId, int? channelGroupId, int? channelId, int? databaseId) =>
        (serverGroupId, channelGroupId, channelId, databaseId) switch
        {
            ({ } sgid, null, null, null) => new(
                $"server group {sgid}", "servergrouppermlist", "servergroupaddperm", "servergroupdelperm",
                Ids(("sgid", sgid)), SupportsNegate: true, SupportsSkip: true),
            (null, { } cgid, null, null) => new(
                $"channel group {cgid}", "channelgrouppermlist", "channelgroupaddperm", "channelgroupdelperm",
                Ids(("cgid", cgid)), SupportsNegate: false, SupportsSkip: false),
            (null, null, { } cid, null) => new(
                $"channel {cid}", "channelpermlist", "channeladdperm", "channeldelperm",
                Ids(("cid", cid)), SupportsNegate: false, SupportsSkip: false),
            (null, null, null, { } cldbid) => new(
                $"client {cldbid}", "clientpermlist", "clientaddperm", "clientdelperm",
                Ids(("cldbid", cldbid)), SupportsNegate: false, SupportsSkip: true),
            (null, null, { } cid, { } cldbid) => new(
                $"client {cldbid} in channel {cid}", "channelclientpermlist", "channelclientaddperm", "channelclientdelperm",
                Ids(("cid", cid), ("cldbid", cldbid)), SupportsNegate: false, SupportsSkip: false),
            _ => throw new McpException(Choices),
        };

    private static Dictionary<string, string> Ids(params (string Key, int Value)[] ids) =>
        ids.ToDictionary(id => id.Key, id => ToolArguments.Text(id.Value), StringComparer.Ordinal);
}