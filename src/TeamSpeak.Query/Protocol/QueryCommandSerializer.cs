using System.Text;

namespace TeamSpeak.Query.Protocol;

/// <summary>
/// Renders a <see cref="QueryCommand"/> for each transport.
/// </summary>
/// <remarks>
/// The two interfaces take the same commands in different clothing: SSH wants one escaped text
/// line, WebQuery wants a URL path with a query string. Keeping both renderings here is what makes
/// a <see cref="QueryCommand"/> transport-agnostic.
/// </remarks>
public static class QueryCommandSerializer
{
    /// <summary>Renders a command as a line for the SSH interface.</summary>
    /// <param name="command">The command to render.</param>
    /// <returns>The wire line, without a trailing newline.</returns>
    /// <exception cref="ArgumentException">Thrown when a name would break the wire format.</exception>
    public static string ToWireLine(QueryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateName(command.Name, nameof(command));

        var builder = new StringBuilder(command.Name);

        foreach (var argument in command.Arguments ?? [])
        {
            ValidateName(argument, nameof(command));
            builder.Append(' ').Append(argument);
        }

        foreach (var (key, value) in command.Parameters ?? EmptyParameters)
        {
            ValidateName(key, nameof(command));
            builder.Append(' ').Append(key).Append('=').Append(QueryEscaping.Escape(value));
        }

        foreach (var option in command.Options ?? [])
        {
            builder.Append(' ').Append(NormaliseOption(option));
        }

        return builder.ToString();
    }

    /// <summary>Renders a command as a relative WebQuery URL.</summary>
    /// <param name="command">The command to render.</param>
    /// <param name="virtualServerId">
    /// The virtual server to scope the command to, or <see langword="null"/> for instance-wide
    /// commands such as <c>version</c> and <c>serverlist</c>.
    /// </param>
    /// <returns>A relative URL such as <c>1/clientinfo?clid=3</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when a name would break the URL.</exception>
    /// <exception cref="NotSupportedException">Thrown for a command with positional arguments.</exception>
    /// <remarks>
    /// Values are percent-encoded rather than query-escaped: the WebQuery speaks JSON and HTTP, so
    /// the backslash escapes of the line protocol have no place here.
    /// </remarks>
    public static string ToWebQueryPath(QueryCommand command, int? virtualServerId = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateName(command.Name, nameof(command));

        if (command.Arguments is { Count: > 0 })
        {
            throw new NotSupportedException(
                $"'{command.Name}' takes a positional argument, which the WebQuery has no form for.");
        }

        var builder = new StringBuilder();

        if (virtualServerId is { } sid)
        {
            builder.Append(sid).Append('/');
        }

        builder.Append(command.Name);

        var first = true;
        foreach (var (key, value) in command.Parameters ?? EmptyParameters)
        {
            ValidateName(key, nameof(command));
            builder.Append(first ? '?' : '&').Append(Uri.EscapeDataString(key))
                   .Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }

        foreach (var option in command.Options ?? [])
        {
            builder.Append(first ? '?' : '&').Append(Uri.EscapeDataString(NormaliseOption(option)));
            first = false;
        }

        return builder.ToString();
    }

    private static readonly Dictionary<string, string> EmptyParameters = [];

    private static string NormaliseOption(string option)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(option);

        var bare = option.StartsWith('-') ? option[1..] : option;
        ValidateName(bare, nameof(option));

        return "-" + bare;
    }

    /// <summary>
    /// Rejects command, parameter and option names that are not plain identifiers.
    /// </summary>
    /// <remarks>
    /// Values are escaped and so can hold anything, but names are written to the wire verbatim.
    /// A name carrying a space or a newline would be read by the server as a further command,
    /// which matters because <c>ts_query_raw</c> lets a model choose these names.
    /// </remarks>
    private static void ValidateName(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')
            {
                throw new ArgumentException(
                    $"'{name}' is not a valid ServerQuery name; expected letters, digits, '_' or '-'.",
                    parameterName);
            }
        }
    }
}