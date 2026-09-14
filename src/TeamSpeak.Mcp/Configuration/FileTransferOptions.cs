namespace TeamSpeak.Mcp.Configuration;

/// <summary>
/// Where file transfer tools may read and write on this machine, and how much they pass inline.
/// </summary>
/// <remarks>
/// A model chooses the paths, and over Streamable HTTP it does so from another machine, so local
/// files are off unless a directory is configured, and every local path is resolved inside it.
/// </remarks>
public sealed class FileTransferOptions
{
    /// <summary>The default limit for content passed inline, 1 MiB.</summary>
    public const int DefaultMaxInlineBytes = 1024 * 1024;

    /// <summary>
    /// Gets or sets the directory <c>localPath</c> arguments are resolved in. Unset, local paths are
    /// refused and files travel inline only.
    /// </summary>
    public string? LocalDirectory { get; set; }

    /// <summary>
    /// Gets or sets the largest file, in bytes, uploaded from or downloaded into a tool answer
    /// rather than a local file.
    /// </summary>
    public int MaxInlineBytes { get; set; } = DefaultMaxInlineBytes;
}
