using TeamSpeak.Query.Client;
using TeamSpeak.Query.Transport;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// Holds the one SSH session the integration tests share.
/// </summary>
/// <remarks>
/// Opening a connection per test is exactly the mistake this project exists to avoid: six tests
/// connecting at once is a burst, and the server blocks the client IP for minutes. Sharing a single
/// session also mirrors how the MCP server itself behaves.
/// </remarks>
public sealed class LiveServerFixture : IAsyncLifetime
{
    private SshQueryTransport? _ssh;

    /// <summary>Gets the shared SSH transport.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no SSH-capable server is configured.</exception>
    public SshQueryTransport Ssh =>
        _ssh ?? throw new InvalidOperationException(
            "No SSH session. This test should have been skipped; check its skip attribute.");

    /// <summary>Builds a profile pointing at the configured live server.</summary>
    /// <returns>The profile.</returns>
    public static QueryProfile Profile() => new()
    {
        Name = "integration",
        Host = LiveServer.Host,
        Password = LiveServer.Password,
        WebQueryUrl = LiveServer.WebQueryUrl,
        ApiKey = LiveServer.ApiKey,
    };

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (LiveServer.CanUseSsh)
        {
            _ssh = await SshQueryTransport.ConnectAsync(Profile(), TestContext.Current.CancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ssh is not null)
        {
            await _ssh.DisposeAsync();
        }
    }
}