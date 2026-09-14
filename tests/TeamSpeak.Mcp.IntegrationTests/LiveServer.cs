namespace TeamSpeak.Mcp.IntegrationTests;

/// <summary>
/// The live server the integration suite runs against, taken from the environment.
/// </summary>
/// <remarks>
/// Nothing here is committed. Point the suite at a disposable TeamSpeak 6 instance — these tests
/// connect for real, and some of them will eventually write.
/// </remarks>
internal static class LiveServer
{
    /// <summary>The variable naming the server. Its absence skips the whole suite.</summary>
    public const string HostVariable = "TSMCP_TEST_HOST";

    /// <summary>The variable holding the query admin password, needed for the SSH tests.</summary>
    public const string PasswordVariable = "TSMCP_TEST_PASSWORD";

    /// <summary>The variable holding an API key, needed for the WebQuery tests.</summary>
    public const string ApiKeyVariable = "TSMCP_TEST_APIKEY";

    /// <summary>
    /// The variable that allows tests which disturb a real person on the server, such as kicking a
    /// connected client off it.
    /// </summary>
    public const string DisruptiveVariable = "TSMCP_TEST_DISRUPTIVE";

    /// <summary>Gets a value indicating whether disruptive tests may run.</summary>
    public static bool AllowsDisruptiveTests => Get(DisruptiveVariable) == "1";

    /// <summary>Gets the configured host, or an empty string when the suite is disabled.</summary>
    public static string Host => Get(HostVariable) ?? string.Empty;

    /// <summary>Gets the configured query admin password, if any.</summary>
    public static string? Password => Get(PasswordVariable);

    /// <summary>Gets the configured API key, if any.</summary>
    public static string? ApiKey => Get(ApiKeyVariable);

    /// <summary>Gets the WebQuery base address, if a host is configured.</summary>
    public static Uri? WebQueryUrl =>
        Host.Length == 0 ? null : new Uri($"http://{Host}:10080");

    /// <summary>Gets a value indicating whether the SSH tests can run.</summary>
    public static bool CanUseSsh => Host.Length > 0 && !string.IsNullOrEmpty(Password);

    /// <summary>Gets a value indicating whether the WebQuery tests can run.</summary>
    public static bool CanUseWebQuery => Host.Length > 0 && !string.IsNullOrEmpty(ApiKey);

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}