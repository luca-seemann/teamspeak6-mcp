using System.Runtime.CompilerServices;

namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// A fact that runs only when a live TeamSpeak 6 server is configured for the test run.
/// </summary>
/// <remarks>
/// Set <c>TSMCP_TEST_HOST</c> to the server address to enable these tests. Without it they report as
/// skipped, so the suite stays green on machines and CI runners that have no TeamSpeak server.
/// </remarks>
public sealed class RequiresTeamSpeakServerFactAttribute : FactAttribute
{
    /// <summary>The environment variable that points the integration suite at a live server.</summary>
    public const string HostVariable = "TSMCP_TEST_HOST";

    /// <summary>Initialises the attribute, skipping the test when no server is configured.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; used for test source information.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; used for test source information.</param>
    public RequiresTeamSpeakServerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostVariable)))
        {
            Skip = $"No live TeamSpeak server configured. Set {HostVariable} to run this test.";
        }
    }
}