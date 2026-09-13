namespace TeamSpeak.Query.Protocol;

/// <summary>
/// Thrown when a response cannot be decoded because it does not match the protocol.
/// </summary>
/// <remarks>
/// This signals malformed or truncated data on the wire. A command the server understood but
/// refused is not an exception: it comes back as a <see cref="QueryResponse"/> whose
/// <see cref="QueryError"/> carries a non-zero id.
/// </remarks>
public sealed class QueryProtocolException : Exception
{
    /// <summary>Initialises a new instance.</summary>
    public QueryProtocolException()
        : base("The ServerQuery response could not be decoded.")
    {
    }

    /// <summary>Initialises a new instance with a message.</summary>
    /// <param name="message">A description of the decoding failure.</param>
    public QueryProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and an underlying cause.</summary>
    /// <param name="message">A description of the decoding failure.</param>
    /// <param name="innerException">The underlying cause.</param>
    public QueryProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}