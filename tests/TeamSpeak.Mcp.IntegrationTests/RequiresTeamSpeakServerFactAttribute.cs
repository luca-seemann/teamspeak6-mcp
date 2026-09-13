using System.Runtime.CompilerServices;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// A fact that runs only when a live TeamSpeak 6 server with SSH credentials is configured.
/// </summary>
/// <remarks>
/// Set <c>TSMCP_TEST_HOST</c> and <c>TSMCP_TEST_PASSWORD</c> to enable these. Without them they
/// report as skipped, so the suite stays green on machines and CI runners that have no server.
/// </remarks>
public sealed class RequiresTeamSpeakServerFactAttribute : FactAttribute
{
    /// <summary>The environment variable that points the integration suite at a live server.</summary>
    public const string HostVariable = LiveServer.HostVariable;

    /// <summary>Initialises the attribute, skipping the test when no SSH-capable server is configured.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; used for test source information.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; used for test source information.</param>
    public RequiresTeamSpeakServerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!LiveServer.CanUseSsh)
        {
            Skip = $"No live TeamSpeak server configured. Set {LiveServer.HostVariable} and " +
                   $"{LiveServer.PasswordVariable} to run this test.";
        }
    }
}

/// <summary>
/// A fact that runs only when a live server and a WebQuery API key are configured.
/// </summary>
/// <remarks>
/// API keys can only be minted over SSH with <c>apikeyadd</c>, so these tests need both
/// <c>TSMCP_TEST_HOST</c> and <c>TSMCP_TEST_APIKEY</c>.
/// </remarks>
public sealed class RequiresWebQueryFactAttribute : FactAttribute
{
    /// <summary>Initialises the attribute, skipping the test when no API key is configured.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; used for test source information.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; used for test source information.</param>
    public RequiresWebQueryFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!LiveServer.CanUseWebQuery || !LiveServer.CanUseSsh)
        {
            Skip = $"No live WebQuery configured. Set {LiveServer.HostVariable}, " +
                   $"{LiveServer.PasswordVariable} and {LiveServer.ApiKeyVariable} to run this test.";
        }
    }
}