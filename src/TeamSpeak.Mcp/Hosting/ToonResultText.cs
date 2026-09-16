using System.Text.Json;

using Cysharp.AI;

using Microsoft.Extensions.Configuration;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Configuration;

namespace TeamSpeak.Mcp.Hosting;

/// <summary>
/// Returns tool results as text only, written as TOON wherever that is shorter than JSON.
/// </summary>
/// <remarks>
/// <para>
/// By default a result carries its data twice: as structured content matching the tool's output
/// schema, and as a JSON text block. Measured with Claude Code 2.1.268, the model is given the
/// structured content whenever there is some, serialized as JSON, and the text block is discarded. A
/// TOON text block therefore only reaches the model when the result has no structured content, and a
/// tool that declares an output schema must return structured content. This mode drops both.
/// </para>
/// <para>
/// Measured, the 425 permissions of the Server Admin group then cost the model about 10,100 tokens
/// instead of 14,700. A channel tree or a single object would be longer as TOON and stays JSON text.
/// The cost is that clients which read typed results get none; for a client that only hands text to
/// its model, such as Claude Code, nothing is lost.
/// </para>
/// </remarks>
public static class ToonResultText
{
    /// <summary>The configuration key choosing the format.</summary>
    public const string ConfigurationKey = "TeamSpeak:ToolResultText";

    /// <summary>What the server instructions add when results are written as TOON.</summary>
    public const string InstructionsNote =
        "- Tool results are written as TOON where that is shorter than JSON, and as JSON otherwise. TOON has " +
        "`key: value` lines, and lists of uniform records as a header `name[count]{field,field}:` followed " +
        "by one comma-separated line per record. Quoted values are JSON strings.";

    /// <summary>Reads the configured format.</summary>
    /// <param name="configuration">The configuration root.</param>
    /// <returns>The format; <see cref="ToolResultTextFormat.Json"/> when none is configured.</returns>
    /// <exception cref="InvalidOperationException">Thrown for a value that is not a format.</exception>
    public static ToolResultTextFormat Configured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            return ToolResultTextFormat.Json;
        }

        return Enum.TryParse<ToolResultTextFormat>(value.Trim(), ignoreCase: true, out var format) && Enum.IsDefined(format)
            ? format
            : throw new InvalidOperationException(
                $"{ConfigurationKey} is '{value}', which is not a format. Use {string.Join(" or ", Enum.GetNames<ToolResultTextFormat>())}.");
    }

    /// <summary>Wraps the call-tool pipeline so successful results are rewritten.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A handler that rewrites the text block of each result.</returns>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return async (request, cancellationToken) => Rewrite(await next(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Removes the output schema a tool declares, since results in this mode carry no structured content.</summary>
    /// <param name="tool">The tool to change.</param>
    public static void DropOutputSchema(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        tool.OutputSchema = null;
    }

    /// <summary>Turns a result into text only: TOON when that is shorter, the JSON text otherwise.</summary>
    /// <param name="result">A tool result.</param>
    /// <returns>The same result, without structured content.</returns>
    /// <remarks>
    /// <para>
    /// Once the output schema is gone the SDK produces no structured content, so the data is read from
    /// the JSON text block, which holds the same. Structured content that is still present, from a
    /// tool that sets it itself, is removed: Claude Code would prefer it to the text.
    /// </para>
    /// <para>
    /// Errors, and results whose content is anything but one text block of JSON, are left alone. When
    /// encoding fails, the JSON text stays; a formatting problem must never cost a tool result.
    /// </para>
    /// </remarks>
    public static CallToolResult Rewrite(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsError == true || result.Content is not [TextContentBlock text])
        {
            return result;
        }

        result.StructuredContent = null;

        string toon;
        try
        {
            using var document = JsonDocument.Parse(text.Text);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return result;
            }

            toon = ToonEncoder.Encode(document.RootElement);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Plain text such as "Deleted API key 36." is not JSON and stays as it is.
            return result;
        }

        if (toon.Length < text.Text.Length)
        {
            result.Content = [new TextContentBlock { Text = toon }];
        }

        return result;
    }
}