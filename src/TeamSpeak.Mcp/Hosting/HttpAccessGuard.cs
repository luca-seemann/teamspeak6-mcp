using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using TeamSpeak.Mcp.Configuration;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Decides who may use the Streamable HTTP endpoint, before any MCP message is read.
/// </summary>
/// <remarks>
/// <para>
/// The MCP specification requires a Streamable HTTP server to validate the <c>Origin</c> header of
/// every request and to refuse an invalid one with 403. Without that, a web page open in the user's
/// browser can reach a server listening on localhost through DNS rebinding and call its tools. Before
/// this guard existed, a request with <c>Origin: http://evil.example</c> and a forged <c>Host</c> was
/// answered with 200, tool list included.
/// </para>
/// <para>
/// Three checks run in order. A foreign <c>Host</c> is refused when no token is configured, a
/// foreign <c>Origin</c> always, and a missing or wrong bearer token whenever one is configured.
/// A server that would be reachable from other machines without a token does not start at all.
/// </para>
/// </remarks>
public sealed class HttpAccessGuard
{
    private readonly byte[]? _tokenHash;
    private readonly HashSet<string> _allowedOrigins;
    private readonly HashSet<string> _allowedHosts;

    /// <summary>Initialises the guard, refusing a configuration that would leave the endpoint open.</summary>
    /// <param name="options">The HTTP settings.</param>
    /// <param name="bindUrl">The URL the endpoint is bound to.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the token is too short, an allowed origin is not a URL, or the endpoint is bound to
    /// a non-loopback address without a token.
    /// </exception>
    public HttpAccessGuard(HttpOptions options, string bindUrl)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bindUrl);

        if (!string.IsNullOrWhiteSpace(options.BearerToken))
        {
            var token = options.BearerToken.Trim();
            if (token.Length < HttpOptions.MinimumTokenLength)
            {
                throw new InvalidOperationException(
                    $"TeamSpeak:Http:BearerToken has {token.Length} characters; use a random value of at least " +
                    $"{HttpOptions.MinimumTokenLength}, for example the output of 'openssl rand -hex 32'.");
            }

            _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }
        else if (!IsLoopbackUrl(bindUrl))
        {
            throw new InvalidOperationException(
                $"Streamable HTTP is bound to '{bindUrl}', which other machines can reach, but no bearer token " +
                "is configured, so anyone reaching the port could use every tool the safety level allows. Set " +
                "TeamSpeak:Http:BearerToken (environment variable TSMCP_TeamSpeak__Http__BearerToken) to a random " +
                $"value of at least {HttpOptions.MinimumTokenLength} characters, or bind to 127.0.0.1.");
        }

        _allowedOrigins = new HashSet<string>(
            options.AllowedOrigins.Where(origin => !string.IsNullOrWhiteSpace(origin)).Select(ConfiguredOrigin),
            StringComparer.OrdinalIgnoreCase);

        _allowedHosts = new HashSet<string>(
            options.AllowedHosts.Where(host => !string.IsNullOrWhiteSpace(host)).Select(host => host.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runs the checks, answering a refused request itself.</summary>
    /// <param name="context">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A task that completes when the request is handled.</returns>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var refusal = Check(context.Request);
        if (refusal is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var (status, message) = refusal.Value;
        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
        }

        // The specification allows a JSON-RPC error without an id as the body of a refused request.
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", error = new { code = -32000, message } }),
            context.RequestAborted).ConfigureAwait(false);
    }

    private (int Status, string Message)? Check(HttpRequest request)
    {
        if (_tokenHash is null && !IsAllowedHost(request.Host.Host))
        {
            return (StatusCodes.Status403Forbidden,
                $"Host '{request.Host.Host}' is not allowed. Without a bearer token only loopback names and TeamSpeak:Http:AllowedHosts are accepted.");
        }

        if (request.Headers.Origin.ToString() is { Length: > 0 } origin && !IsAllowedOrigin(origin))
        {
            return (StatusCodes.Status403Forbidden,
                $"Origin '{origin}' is not allowed. Add it to TeamSpeak:Http:AllowedOrigins if this page may use the server.");
        }

        if (_tokenHash is not null && !PresentsToken(request.Headers.Authorization.ToString()))
        {
            return (StatusCodes.Status401Unauthorized,
                "A bearer token is required: send 'Authorization: Bearer <TeamSpeak:Http:BearerToken>'.");
        }

        return null;
    }

    private bool PresentsToken(string authorization)
    {
        const string Scheme = "Bearer ";
        if (!authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Comparing fixed-length hashes keeps the comparison constant-time whatever length was sent.
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[Scheme.Length..].Trim()));
        return CryptographicOperations.FixedTimeEquals(presented, _tokenHash);
    }

    private bool IsAllowedHost(string host) => IsLoopbackHost(host) || _allowedHosts.Contains(host);

    private bool IsAllowedOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && (IsLoopbackHost(uri.Host) || _allowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority)));

    private static string ConfiguredOrigin(string origin) =>
        Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.GetLeftPart(UriPartial.Authority)
            : throw new InvalidOperationException(
                $"TeamSpeak:Http:AllowedOrigins contains '{origin}', which is not an origin such as https://admin.example.com.");

    private static bool IsLoopbackUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host);

    private static bool IsLoopbackHost(string host)
    {
        var bare = host.Trim().TrimStart('[').TrimEnd(']');
        return string.Equals(bare, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(bare, out var address) && IPAddress.IsLoopback(address));
    }
}