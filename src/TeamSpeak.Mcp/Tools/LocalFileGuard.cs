using ModelContextProtocol;

namespace TeamSpeak.Mcp.Tools;

/// <summary>
/// Opens local files for file transfers, and checks the opened file rather than its path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileTransferSupport.LocalPath"/> refuses paths outside the local directory early and
/// readably. It cannot see what happens between its check and the open: a part of the path swapped
/// for a link, a <c>.partial</c> file planted as a link, or a hard link to a file elsewhere, which
/// looks like any other file by its path.
/// </para>
/// <para>
/// So every open goes through here. After opening, the file the operating system actually opened must
/// lie inside the directory, resolved the same way, and have exactly one name. Otherwise the stream is
/// closed before a byte is read or written. A file created by <see cref="CreateNew"/> that turns out
/// to lie elsewhere stays behind empty.
/// </para>
/// </remarks>
public static class LocalFileGuard
{
    private const int BufferSize = 81920;

    /// <summary>Opens an existing file for reading.</summary>
    /// <param name="root">The local directory, as <see cref="FileTransferSupport.LocalRoot"/> returns it.</param>
    /// <param name="path">A path <see cref="FileTransferSupport.LocalPath"/> accepted.</param>
    /// <returns>The stream.</returns>
    public static FileStream OpenRead(string root, string path) =>
        Checked(root, path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true));

    /// <summary>Creates a file that must not exist yet, with its directories.</summary>
    /// <param name="root">The local directory.</param>
    /// <param name="path">A path <see cref="FileTransferSupport.LocalPath"/> accepted.</param>
    /// <returns>The stream.</returns>
    public static FileStream CreateNew(string root, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return Checked(root, path, new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true));
    }

    /// <summary>Opens an existing file to append to it.</summary>
    /// <param name="root">The local directory.</param>
    /// <param name="path">A path <see cref="FileTransferSupport.LocalPath"/> accepted.</param>
    /// <returns>The stream, positioned at the end.</returns>
    public static FileStream OpenAppend(string root, string path) =>
        Checked(root, path, new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, BufferSize, useAsync: true));

    private static FileStream Checked(string root, string path, FileStream stream)
    {
        try
        {
            var directory = NativeFileIdentity.FinalDirectoryPath(root);
            var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            var actual = NativeFileIdentity.FinalPath(stream.SafeFileHandle);
            if (!actual.StartsWith(prefix, comparison))
            {
                throw new McpException(
                    $"{path} resolves to {actual}, outside {root}, through a link that appeared in the path. " +
                    "Nothing was read or written.");
            }

            if (NativeFileIdentity.LinkCount(stream.SafeFileHandle) > 1)
            {
                throw new McpException(
                    $"{path} is a hard link: the same file also exists under another name, possibly outside {root}. " +
                    "Nothing was read or written.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}