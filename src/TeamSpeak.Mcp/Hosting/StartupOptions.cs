namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Command line options that decide how the process starts up.
/// </summary>
/// <param name="Transport">The MCP transport to serve.</param>
/// <param name="HttpUrl">The URL to bind when <paramref name="Transport"/> is <see cref="TransportMode.Http"/>.</param>
public sealed record StartupOptions(TransportMode Transport, string HttpUrl)
{
    /// <summary>The URL the HTTP transport binds to unless <c>--url</c> overrides it.</summary>
    public const string DefaultHttpUrl = "http://127.0.0.1:7801";

    /// <summary>
    /// Parses the process arguments, falling back to stdio on the loopback default.
    /// </summary>
    /// <param name="args">The raw command line arguments.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="ArgumentException">Thrown when an option has an unrecognised or missing value.</exception>
    public static StartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var transport = TransportMode.Stdio;
        var url = DefaultHttpUrl;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--transport":
                    transport = ParseTransport(ValueAt(args, ++i, "--transport"));
                    break;
                case "--url":
                    url = ValueAt(args, ++i, "--url");
                    break;
                default:
                    throw new ArgumentException($"Unrecognised argument '{args[i]}'.", nameof(args));
            }
        }

        return new StartupOptions(transport, url);
    }

    private static string ValueAt(string[] args, int index, string option) =>
        index < args.Length
            ? args[index]
            : throw new ArgumentException($"Option '{option}' requires a value.", nameof(args));

    private static TransportMode ParseTransport(string value) => value.ToLowerInvariant() switch
    {
        "stdio" => TransportMode.Stdio,
        "http" => TransportMode.Http,
        _ => throw new ArgumentException($"Unknown transport '{value}'. Expected 'stdio' or 'http'.", nameof(value)),
    };
}