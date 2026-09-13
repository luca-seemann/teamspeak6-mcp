using System.Globalization;
using System.Text.RegularExpressions;

namespace TeamSpeak.Query.Protocol;

/// <summary>
/// The status that terminates every ServerQuery response.
/// </summary>
/// <remarks>
/// Over SSH this is the trailing <c>error id=… msg=…</c> line; over WebQuery it is the
/// <c>status</c> object. Both decode to this type.
/// </remarks>
/// <param name="Id">The numeric status code. Zero means success.</param>
/// <param name="Message">The status message, for example <c>ok</c>.</param>
/// <param name="ExtraMessage">A clarifying message the server appends to some failures.</param>
public sealed partial record QueryError(int Id, string Message, string? ExtraMessage = null)
{
    /// <summary>Gets a value indicating whether the command succeeded.</summary>
    public bool IsSuccess => Id == QueryErrorCode.Ok;

    /// <summary>
    /// Gets a value indicating whether the command was rejected because the client is sending too
    /// quickly.
    /// </summary>
    public bool IsFlooding => Id == QueryErrorCode.Flooding;

    /// <summary>
    /// Gets a value indicating whether the query succeeded but matched no rows.
    /// </summary>
    /// <remarks>
    /// Routine for list commands on an empty server. Callers should present this as an empty
    /// result, not as a failure.
    /// </remarks>
    public bool IsEmptyResult => Id == QueryErrorCode.EmptyResultSet;

    /// <summary>
    /// Gets how long the server asked the client to wait before sending again.
    /// </summary>
    /// <returns>
    /// The requested delay when the server supplied one, otherwise <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// A flood rejection carries the delay in its extra message, as in
    /// <c>please wait 1 seconds</c>. Honouring it matters: continuing to send through a flood
    /// rejection escalates to an IP-level block affecting both query interfaces.
    /// </remarks>
    public TimeSpan? RetryAfter
    {
        get
        {
            if (ExtraMessage is null)
            {
                return null;
            }

            var match = WaitSecondsPattern().Match(ExtraMessage);
            return match.Success
                   && int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
    }

    [GeneratedRegex(@"please wait (\d+) second", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WaitSecondsPattern();
}