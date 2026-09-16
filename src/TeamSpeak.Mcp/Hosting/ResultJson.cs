using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using ModelContextProtocol;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// How tool, prompt and resource results are serialized.
/// </summary>
/// <remarks>
/// <para>
/// The SDK's default options escape every character outside ASCII. Measured on the test server, a
/// channel named "🔒 Admin 🔒" came back as <c>\uD83D\uDD12 Admin \uD83D\uDD12</c>: twelve characters per
/// emoji and six per umlaut, in the text block and the structured content alike, and harder for a
/// model to read. Even <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> still escapes every
/// emoji, so <see cref="MinimalJsonEncoder"/> escapes only what JSON itself requires.
/// </para>
/// <para>
/// The results travel as JSON-RPC over stdio, or as <c>application/json</c> and
/// <c>text/event-stream</c> in UTF-8, never inside HTML or a script, so no further escaping is needed.
/// </para>
/// </remarks>
public static class ResultJson
{
    /// <summary>Gets the SDK's options, writing text as it is apart from what JSON requires.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            Encoder = MinimalJsonEncoder.Instance,
        };

        options.MakeReadOnly();
        return options;
    }
}

/// <summary>
/// Escapes only what a JSON string cannot hold literally: the quote, the backslash, control characters
/// and unpaired surrogates.
/// </summary>
public sealed class MinimalJsonEncoder : JavaScriptEncoder
{
    /// <summary>Gets the shared instance.</summary>
    public static MinimalJsonEncoder Instance { get; } = new();

    private MinimalJsonEncoder()
    {
    }

    /// <inheritdoc />
    public override int MaxOutputCharactersPerInputCharacter => 12;

    /// <inheritdoc />
    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        var span = new ReadOnlySpan<char>(text, textLength);
        for (var index = 0; index < span.Length;)
        {
            if (Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed) != OperationStatus.Done || WillEncode(rune.Value))
            {
                return index;
            }

            index += consumed;
        }

        return -1;
    }

    /// <inheritdoc />
    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var output = new Span<char>(buffer, bufferLength);
        numberOfCharactersWritten = 0;

        if (!WillEncode(unicodeScalar))
        {
            if (!new Rune(unicodeScalar).TryEncodeToUtf16(output, out numberOfCharactersWritten))
            {
                return false;
            }

            return true;
        }

        var escape = unicodeScalar switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            '\b' => "\\b",
            '\f' => "\\f",
            _ => $"\\u{unicodeScalar:X4}",
        };

        if (escape.Length > output.Length)
        {
            return false;
        }

        escape.CopyTo(output);
        numberOfCharactersWritten = escape.Length;
        return true;
    }

    /// <inheritdoc />
    public override bool WillEncode(int unicodeScalar) =>
        unicodeScalar is < 0x20 or '"' or '\\' or (>= 0xD800 and <= 0xDFFF);
}