using System.Buffers;
using System.Text;

namespace TeamSpeak.Query.Protocol;

/// <summary>
/// Escapes and unescapes values for the line-based ServerQuery protocol.
/// </summary>
/// <remarks>
/// The protocol separates parameters with spaces and records with <c>|</c>, so any such character
/// inside a value has to be escaped. Escaping applies to error messages too, not just data: a real
/// server answers an empty result set with <c>error id=1281 msg=database\sempty\sresult\sset</c>.
/// The WebQuery interface returns plain JSON and needs none of this.
/// </remarks>
public static class QueryEscaping
{
    private static readonly SearchValues<char> EscapableChars =
        SearchValues.Create("\\/ |\a\b\f\n\r\t\v");

    /// <summary>Escapes a value for transmission over the line-based protocol.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The escaped value.</returns>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Most values contain nothing that needs escaping, so avoid allocating when possible.
        var index = value.AsSpan().IndexOfAny(EscapableChars);
        if (index < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        builder.Append(value, 0, index);

        for (var i = index; i < value.Length; i++)
        {
            var c = value[i];
            var escape = EscapeFor(c);
            if (escape is not '\0')
            {
                builder.Append('\\').Append(escape);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Unescapes a value received over the line-based protocol.</summary>
    /// <param name="value">The escaped value.</param>
    /// <returns>The raw value.</returns>
    /// <remarks>
    /// A backslash followed by an unrecognised character is passed through unchanged rather than
    /// dropped, and a trailing backslash is preserved, so malformed input never silently loses data.
    /// </remarks>
    public static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var index = value.IndexOf('\\', StringComparison.Ordinal);
        if (index < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, index);

        for (var i = index; i < value.Length; i++)
        {
            if (value[i] is not '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            var unescaped = UnescapeFor(value[i + 1]);
            if (unescaped is not '\0')
            {
                builder.Append(unescaped);
                i++;
            }
            else
            {
                builder.Append(value[i]);
            }
        }

        return builder.ToString();
    }

    private static char EscapeFor(char c) => c switch
    {
        '\\' => '\\',
        '/' => '/',
        ' ' => 's',
        '|' => 'p',
        '\a' => 'a',
        '\b' => 'b',
        '\f' => 'f',
        '\n' => 'n',
        '\r' => 'r',
        '\t' => 't',
        '\v' => 'v',
        _ => '\0',
    };

    private static char UnescapeFor(char c) => c switch
    {
        '\\' => '\\',
        '/' => '/',
        's' => ' ',
        'p' => '|',
        'a' => '\a',
        'b' => '\b',
        'f' => '\f',
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        'v' => '\v',
        _ => '\0',
    };
}