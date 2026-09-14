using TeamSpeak.Mcp.Configuration;
using TeamSpeak.Mcp.Safety;
using TeamSpeak.Mcp.Tools;
using TeamSpeak.Query.Client;
using TeamSpeak.Query.FakeServer;
using TeamSpeak.Query.Protocol;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>
/// Wires tools to a fake transport through the real executor, connection manager and safety policy.
/// </summary>
internal sealed class ToolHarness : IAsyncDisposable
{
    private readonly QueryConnectionManager _connections;

    public ToolHarness(SafetyLevel safety = SafetyLevel.ReadOnly, QueryProfile? profile = null)
    {
        _connections = new QueryConnectionManager(
            new ProfileRegistry([profile ?? new QueryProfile { Name = "test", Host = "ts.example.com", Password = "secret" }]),
            (_, _, _) => Task.FromResult<IQueryTransport>(Transport));

        Executor = new QueryExecutor(_connections, new SafetyPolicy(safety));
    }

    public FakeQueryTransport Transport { get; } = new();

    public QueryExecutor Executor { get; }

    public static QueryResponse Records(params Dictionary<string, string>[] records) =>
        new(records.Select(fields => new QueryRecord(fields)).ToList(), new QueryError(QueryErrorCode.Ok, "ok"));

    public static QueryResponse Error(int id, string message, string? extra = null) =>
        new([], new QueryError(id, message, extra));

    public ValueTask DisposeAsync() => _connections.DisposeAsync();
}