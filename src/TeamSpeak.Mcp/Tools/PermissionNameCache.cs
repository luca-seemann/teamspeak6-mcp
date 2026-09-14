using System.Collections.Concurrent;

using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>
/// Remembers each server's permission names, so tools can show names instead of bare ids.
/// </summary>
/// <remarks>
/// <c>permoverview</c>, <c>permfind</c> and the permission lists identify permissions by number.
/// The mapping comes from <c>permissionlist</c>, which is some 44 kilobytes and fixed for a given
/// server version, so it is fetched once per profile and kept for the life of the process. A failed
/// fetch is not remembered.
/// </remarks>
public sealed class PermissionNameCache : IDisposable
{
    private readonly ConcurrentDictionary<string, PermissionNames> _byProfile = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Gets the permission names for a profile, fetching them on first use.</summary>
    /// <param name="executor">The path to the server.</param>
    /// <param name="profile">The profile, or <see langword="null"/> when only one is configured.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The names.</returns>
    public async Task<PermissionNames> GetAsync(
        QueryExecutor executor,
        string? profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);

        var name = executor.ResolveProfile(profile).Name;
        if (_byProfile.TryGetValue(name, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_byProfile.TryGetValue(name, out cached))
            {
                return cached;
            }

            var records = await executor.RunAsync(
                "reading the permission names",
                SafetyLevel.ReadOnly,
                name,
                new QueryCommand("permissionlist"),
                cancellationToken).ConfigureAwait(false);

            var names = new PermissionNames(records.Select(record => new PermissionDefinition(
                record.GetInt32("permid"),
                record.GetString("permname"),
                record.GetString("permdesc"))));

            _byProfile[name] = names;
            return names;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}

/// <summary>The permissions a server knows, by id and by name.</summary>
public sealed class PermissionNames
{
    private readonly Dictionary<int, PermissionDefinition> _byId = [];
    private readonly Dictionary<string, PermissionDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initialises the lookup.</summary>
    /// <param name="definitions">The permissions, as <c>permissionlist</c> returns them.</param>
    public PermissionNames(IEnumerable<PermissionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var definition in definitions)
        {
            _byId.TryAdd(definition.Id, definition);
            _byName.TryAdd(definition.Name, definition);
        }
    }

    /// <summary>Gets every permission, in id order.</summary>
    public IReadOnlyList<PermissionDefinition> All => [.. _byId.Values.OrderBy(definition => definition.Id)];

    /// <summary>Gets the name of a permission, or a placeholder naming its id when it is unknown.</summary>
    /// <param name="id">The permission id.</param>
    /// <returns>The name.</returns>
    public string NameOf(int id) => _byId.TryGetValue(id, out var definition) ? definition.Name : $"#{id}";

    /// <summary>Finds a permission by its name.</summary>
    /// <param name="name">The name, for example <c>i_client_talk_power</c>.</param>
    /// <param name="definition">The permission, when found.</param>
    /// <returns><see langword="true"/> when the server knows the permission.</returns>
    public bool TryFind(string name, out PermissionDefinition definition) =>
        _byName.TryGetValue(name, out definition!);
}

/// <summary>A permission the server knows.</summary>
/// <param name="Id">The numeric id.</param>
/// <param name="Name">The name, for example <c>i_client_talk_power</c>.</param>
/// <param name="Description">What the permission controls.</param>
public sealed record PermissionDefinition(int Id, string Name, string Description);