using System.Globalization;
using System.Text;

namespace TeamSpeak.Query.Protocol;

/// <summary>
/// The version a TeamSpeak server reports, in a form that can be compared.
/// </summary>
/// <remarks>
/// <para>
/// The <c>version</c> command answers with a string such as <c>6.0.0-beta13</c>: a numeric core and,
/// while the series is in beta, a pre-release part like <c>beta12.1</c>. Some behaviour differs
/// between betas, and one command crashed servers before a certain build, so a caller sometimes
/// has to ask whether the server it is talking to is at least a given version.
/// </para>
/// <para>
/// Comparison follows the parts of the string rather than the <c>build</c> timestamp, which is a
/// build date and says nothing about ordering between branches. A pre-release sorts before the
/// release it leads to, so <c>6.0.0-beta13</c> is below <c>6.0.0</c>, and inside a pre-release the
/// numbers compare as numbers, so <c>beta12.1</c> is below <c>beta13</c> where text alone would say
/// the opposite.
/// </para>
/// </remarks>
public sealed class ServerVersion : IComparable<ServerVersion>, IEquatable<ServerVersion>
{
    private readonly int[] _core;
    private readonly Part[] _preRelease;

    private ServerVersion(string text, int[] core, Part[] preRelease)
    {
        Text = text;
        _core = core;
        _preRelease = preRelease;
    }

    /// <summary>Gets the version exactly as the server reported it.</summary>
    public string Text { get; }

    /// <summary>Gets a value indicating whether this is a pre-release such as a beta.</summary>
    public bool IsPreRelease => _preRelease.Length > 0;

    /// <summary>Reads a version string.</summary>
    /// <param name="text">A version such as <c>6.0.0-beta13</c>.</param>
    /// <returns>The version, or <see langword="null"/> when it has no numeric core to compare.</returns>
    /// <remarks>
    /// Build metadata after a <c>+</c> is ignored, as is anything after the first space, so the
    /// <c>virtualserver_version</c> form <c>6.0.0-beta13 [Build: 1789645103]</c> reads the same as the
    /// <c>version</c> command's.
    /// </remarks>
    public static ServerVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var reported = trimmed;

        foreach (var cut in (char[])[' ', '+'])
        {
            var at = trimmed.IndexOf(cut);
            if (at >= 0)
            {
                trimmed = trimmed[..at];
            }
        }

        var dash = trimmed.IndexOf('-');
        var core = dash < 0 ? trimmed : trimmed[..dash];
        var preRelease = dash < 0 ? string.Empty : trimmed[(dash + 1)..];

        var numbers = new List<int>();
        foreach (var part in core.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers.Count == 0 ? null : new ServerVersion(reported, [.. numbers], Split(preRelease));
    }

    /// <summary>Reads a version string, refusing one that cannot be compared.</summary>
    /// <param name="text">A version such as <c>6.0.0-beta13</c>.</param>
    /// <returns>The version.</returns>
    /// <exception cref="FormatException">Thrown when the string has no numeric core.</exception>
    public static ServerVersion Parse(string text) =>
        TryParse(text) ?? throw new FormatException($"'{text}' is not a version this can compare.");

    /// <inheritdoc />
    public int CompareTo(ServerVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var i = 0; i < Math.Max(_core.Length, other._core.Length); i++)
        {
            var mine = i < _core.Length ? _core[i] : 0;
            var theirs = i < other._core.Length ? other._core[i] : 0;

            if (mine != theirs)
            {
                return mine.CompareTo(theirs);
            }
        }

        // A release outranks every pre-release of the same core, so 6.0.0 is above 6.0.0-beta13.
        if (_preRelease.Length == 0 || other._preRelease.Length == 0)
        {
            return other._preRelease.Length.CompareTo(_preRelease.Length);
        }

        for (var i = 0; i < Math.Min(_preRelease.Length, other._preRelease.Length); i++)
        {
            var comparison = _preRelease[i].CompareTo(other._preRelease[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    /// <inheritdoc />
    public bool Equals(ServerVersion? other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ServerVersion);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);

        foreach (var number in _core)
        {
            hash.Add(number);
        }

        foreach (var part in _preRelease)
        {
            hash.Add(part.Number);
            hash.Add(part.Text, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Text;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>Whether <paramref name="left"/> is below <paramref name="right"/>.</returns>
    public static bool operator <(ServerVersion? left, ServerVersion? right) => Compare(left, right) < 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>Whether <paramref name="left"/> is above <paramref name="right"/>.</returns>
    public static bool operator >(ServerVersion? left, ServerVersion? right) => Compare(left, right) > 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>Whether <paramref name="left"/> is at most <paramref name="right"/>.</returns>
    public static bool operator <=(ServerVersion? left, ServerVersion? right) => Compare(left, right) <= 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>Whether <paramref name="left"/> is at least <paramref name="right"/>.</returns>
    public static bool operator >=(ServerVersion? left, ServerVersion? right) => Compare(left, right) >= 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>Whether both describe the same version.</returns>
    public static bool operator ==(ServerVersion? left, ServerVersion? right) => Compare(left, right) == 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>Whether they describe different versions.</returns>
    public static bool operator !=(ServerVersion? left, ServerVersion? right) => Compare(left, right) != 0;

    private static int Compare(ServerVersion? left, ServerVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    /// <summary>Splits a pre-release into the runs of letters and digits that compare separately.</summary>
    private static Part[] Split(string preRelease)
    {
        if (preRelease.Length == 0)
        {
            return [];
        }

        var parts = new List<Part>();

        foreach (var identifier in preRelease.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var run = new StringBuilder();
            var digits = char.IsAsciiDigit(identifier[0]);

            foreach (var character in identifier)
            {
                if (char.IsAsciiDigit(character) != digits && run.Length > 0)
                {
                    parts.Add(Part.Of(run.ToString(), digits));
                    run.Clear();
                    digits = !digits;
                }

                run.Append(character);
            }

            parts.Add(Part.Of(run.ToString(), digits));
        }

        return [.. parts];
    }

    /// <summary>One run of a pre-release: either a number or a word.</summary>
    private readonly record struct Part(string? Text, long Number)
    {
        public static Part Of(string run, bool digits) =>
            digits && long.TryParse(run, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? new Part(null, number)
                : new Part(run, -1);

        // Numbers sort below words, as in semantic versioning, and never compare as text.
        public int CompareTo(Part other) => (Text, other.Text) switch
        {
            (null, null) => Number.CompareTo(other.Number),
            (null, not null) => -1,
            (not null, null) => 1,
            var (mine, theirs) => string.Compare(mine, theirs, StringComparison.OrdinalIgnoreCase),
        };
    }
}