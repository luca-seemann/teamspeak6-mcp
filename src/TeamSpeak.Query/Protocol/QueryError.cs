namespace TeamSpeak.Query.Protocol;

/// <summary>
/// The trailing <c>error id=… msg=…</c> status that terminates every ServerQuery response.
/// </summary>
/// <param name="Id">The numeric error id. Zero means success.</param>
/// <param name="Message">The human readable error message, for example <c>ok</c>.</param>
/// <param name="ExtraMessage">An optional clarifying message the server appends to some failures.</param>
public sealed record QueryError(int Id, string Message, string? ExtraMessage = null)
{
    /// <summary>Gets a value indicating whether the command succeeded.</summary>
    public bool IsSuccess => Id == 0;
}