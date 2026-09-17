using System.Text;

namespace TeamSpeak.Query.Transport;

/// <summary>
/// Decides whether the SSH host key a server presents belongs to the server a profile means.
/// </summary>
/// <remarks>
/// Without this check anyone on the network path can answer in the server's place and receive the
/// <c>serveradmin</c> password, which SSH sends only after the key exchange.
/// </remarks>
public interface IHostKeyVerifier
{
    /// <summary>Checks a presented host key.</summary>
    /// <param name="host">The host name or address the profile connects to.</param>
    /// <param name="port">The SSH query port.</param>
    /// <param name="keyType">The key algorithm, such as <c>ssh-ed25519</c>.</param>
    /// <param name="fingerprint">The key's SHA-256 fingerprint as <c>SHA256:</c> followed by unpadded base64.</param>
    /// <returns>Whether to trust the key, and why not when it is refused.</returns>
    HostKeyVerdict Verify(string host, int port, string keyType, string fingerprint);
}

/// <summary>The outcome of checking a host key.</summary>
/// <param name="Trusted">Whether the connection may continue.</param>
/// <param name="Reason">Why the key was refused; <see langword="null"/> when it is trusted.</param>
public sealed record HostKeyVerdict(bool Trusted, string? Reason)
{
    /// <summary>Gets a verdict that trusts the key.</summary>
    public static HostKeyVerdict Trust { get; } = new(true, null);

    /// <summary>Creates a verdict that refuses the key.</summary>
    /// <param name="reason">Why, in words a user can act on.</param>
    /// <returns>The verdict.</returns>
    public static HostKeyVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Thrown when a server presents an SSH host key that does not match the one expected for it.
/// </summary>
/// <remarks>
/// Never retried: the key will not change between attempts, and repeated connections earn a flood
/// block from the server.
/// </remarks>
public sealed class SshHostKeyMismatchException : IOException
{
    /// <summary>Initializes a new instance of the <see cref="SshHostKeyMismatchException"/> class.</summary>
    public SshHostKeyMismatchException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SshHostKeyMismatchException"/> class.</summary>
    /// <param name="message">Why the key was refused.</param>
    public SshHostKeyMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SshHostKeyMismatchException"/> class.</summary>
    /// <param name="message">Why the key was refused.</param>
    /// <param name="innerException">The failure the SSH library reported.</param>
    public SshHostKeyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Formats and compares host key fingerprints.</summary>
public static class HostKeyFingerprint
{
    private const string Prefix = "SHA256:";

    /// <summary>Brings a fingerprint into the form <c>SHA256:</c> plus unpadded base64.</summary>
    /// <param name="fingerprint">A fingerprint with or without the prefix and padding.</param>
    /// <returns>The normalized fingerprint.</returns>
    public static string Normalize(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        var value = fingerprint.Trim();
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Prefix.Length..];
        }

        return Prefix + value.TrimEnd('=');
    }

    /// <summary>Compares two fingerprints regardless of prefix and padding.</summary>
    /// <param name="left">One fingerprint.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they name the same key.</returns>
    public static bool AreEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
}

/// <summary>Trusts exactly one configured fingerprint.</summary>
/// <param name="expected">The fingerprint the server must present.</param>
/// <param name="setting">The setting it came from, named in the refusal.</param>
public sealed class PinnedHostKey(string expected, string setting) : IHostKeyVerifier
{
    /// <inheritdoc />
    public HostKeyVerdict Verify(string host, int port, string keyType, string fingerprint) =>
        HostKeyFingerprint.AreEqual(expected, fingerprint)
            ? HostKeyVerdict.Trust
            : HostKeyVerdict.Refuse(
                $"{host}:{port} presented the SSH host key {HostKeyFingerprint.Normalize(fingerprint)} ({keyType}), but " +
                $"{setting} expects {HostKeyFingerprint.Normalize(expected)}. Nothing was sent, not even the password. " +
                "If the server's key really changed, update the setting.");
}

/// <summary>
/// Remembers the host key each server presented the first time, and refuses a different one later,
/// as OpenSSH does with <c>known_hosts</c>.
/// </summary>
/// <remarks>
/// <para>
/// One line per server: <c>host:port keytype SHA256:fingerprint</c>. Lines are only ever appended;
/// removing a line makes the next connection trust whatever key the server presents.
/// </para>
/// <para>
/// The first connection is trusted without proof, so an attacker already in place at that moment
/// is not detected. A fingerprint configured per profile closes that gap.
/// </para>
/// </remarks>
public sealed class KnownHostsFile : IHostKeyVerifier
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    /// <summary>Initializes a new instance of the <see cref="KnownHostsFile"/> class.</summary>
    /// <param name="path">The file to read and append to; created with its directory when missing.</param>
    public KnownHostsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>Gets the file's full path.</summary>
    public string Path { get; }

    /// <summary>Finds the fingerprint remembered for a server.</summary>
    /// <param name="host">The host name or address.</param>
    /// <param name="port">The SSH query port.</param>
    /// <returns>The fingerprint, or <see langword="null"/> when the server was never seen.</returns>
    public string? Find(string host, int port)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        using var stream = OpenLocked(FileMode.Open);
        return Find(ReadLines(stream), Key(host, port));
    }

    /// <inheritdoc />
    public HostKeyVerdict Verify(string host, int port, string keyType, string fingerprint)
    {
        var key = Key(host, port);
        var presented = HostKeyFingerprint.Normalize(fingerprint);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // Held for the whole read-then-append, so two processes meeting a new server at once cannot
        // both record a key.
        using var stream = OpenLocked(FileMode.OpenOrCreate);

        if (Find(ReadLines(stream), key) is { } remembered)
        {
            return HostKeyFingerprint.AreEqual(remembered, presented)
                ? HostKeyVerdict.Trust
                : HostKeyVerdict.Refuse(
                    $"{host}:{port} presented the SSH host key {presented} ({keyType}), but {Path} remembers " +
                    $"{remembered} for it. Someone may be intercepting the connection, so nothing was sent, not " +
                    "even the password. If the server was reinstalled and its key really changed, remove its " +
                    "line from that file or pin the new key with TeamSpeak:Profiles:<name>:HostKeyFingerprint.");
        }

        stream.Seek(0, SeekOrigin.End);
        var needsNewline = stream.Length > 0 && LastByte(stream) != '\n';
        var line = $"{(needsNewline ? "\n" : string.Empty)}{key} {keyType} {presented}\n";
        stream.Write(Encoding.UTF8.GetBytes(line));
        stream.Flush(flushToDisk: true);

        return HostKeyVerdict.Trust;
    }

    private static string Key(string host, int port) =>
        $"{host.Trim().ToLowerInvariant()}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string? Find(IEnumerable<string> lines, string key) =>
        lines
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 && string.Equals(parts[0], key, StringComparison.Ordinal))
            .Select(parts => parts[2])
            .FirstOrDefault();

    private static List<string> ReadLines(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            {
                lines.Add(line.Trim());
            }
        }

        return lines;
    }

    private static int LastByte(FileStream stream)
    {
        stream.Seek(-1, SeekOrigin.End);
        var last = stream.ReadByte();
        stream.Seek(0, SeekOrigin.End);
        return last;
    }

    private FileStream OpenLocked(FileMode mode)
    {
        var deadline = DateTime.UtcNow + LockWait;
        while (true)
        {
            try
            {
                return new FileStream(Path, mode, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline && File.Exists(Path))
            {
                // Another process holds the file for its own check; it releases it within milliseconds.
                Thread.Sleep(20);
            }
        }
    }
}