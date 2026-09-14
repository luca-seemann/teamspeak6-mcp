using System.Collections;
using System.Globalization;

namespace TeamSpeak.Query.Protocol;

/// <summary>
/// One record of a ServerQuery response, with typed access to its fields.
/// </summary>
/// <remarks>
/// <para>
/// Everything arrives as text on both transports, and TeamSpeak has its own conventions for what
/// that text means: booleans are <c>1</c> and <c>0</c>, absolute times are Unix seconds, and
/// durations are plain seconds. Decoding those in one place keeps the guesswork out of every tool.
/// </para>
/// <para>
/// The record is also an <see cref="IReadOnlyDictionary{TKey,TValue}"/>, so raw field access still
/// works for anything that has no typed accessor.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "A 'record' is the ServerQuery protocol's own term for this; naming it " +
                    "QueryRecordDictionary would describe the implementation rather than the concept.")]
public sealed class QueryRecord : IReadOnlyDictionary<string, string>
{
    private readonly IReadOnlyDictionary<string, string> _fields;

    /// <summary>Initialises a record over a set of decoded fields.</summary>
    /// <param name="fields">The field values, already unescaped.</param>
    public QueryRecord(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = fields;
    }

    /// <inheritdoc />
    public string this[string key] => _fields[key];

    /// <inheritdoc />
    public IEnumerable<string> Keys => _fields.Keys;

    /// <inheritdoc />
    public IEnumerable<string> Values => _fields.Values;

    /// <inheritdoc />
    public int Count => _fields.Count;

    /// <inheritdoc />
    public bool ContainsKey(string key) => _fields.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, out string value) => _fields.TryGetValue(key, out value!);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Gets a text field, or a fallback when it is absent or empty.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="fallback">The value to use when the field is absent or empty.</param>
    /// <returns>The field value.</returns>
    public string GetString(string key, string fallback = "") =>
        TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    /// <summary>Gets an integer field.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="fallback">The value to use when the field is absent, empty or not a number.</param>
    /// <returns>The parsed value.</returns>
    public int GetInt32(string key, int fallback = 0) =>
        TryGetValue(key, out var raw) && int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>Gets a long integer field, used for sizes and traffic counters.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="fallback">The value to use when the field is absent, empty or not a number.</param>
    /// <returns>The parsed value.</returns>
    public long GetInt64(string key, long fallback = 0) =>
        TryGetValue(key, out var raw) && long.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>Gets a decimal field, such as a packet loss ratio or a ping.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="fallback">The value to use when the field is absent, empty or not a number.</param>
    /// <returns>The parsed value.</returns>
    /// <remarks>The server always writes a dot as the decimal separator, as in <c>0.0000</c>.</remarks>
    public double GetDouble(string key, double fallback = 0) =>
        TryGetValue(key, out var raw)
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>Gets a boolean field, which the server writes as <c>1</c> or <c>0</c>.</summary>
    /// <param name="key">The field name.</param>
    /// <param name="fallback">The value to use when the field is absent or empty.</param>
    /// <returns>The parsed value.</returns>
    public bool GetBoolean(string key, bool fallback = false) =>
        TryGetValue(key, out var raw) && raw.Length > 0 ? raw is not "0" : fallback;

    /// <summary>Gets an absolute time the server encodes as Unix seconds.</summary>
    /// <param name="key">The field name.</param>
    /// <returns>The time, or <see langword="null"/> when the field is absent, empty or zero.</returns>
    /// <remarks>Zero means "never" in most TeamSpeak timestamp fields, not the Unix epoch.</remarks>
    public DateTimeOffset? GetUnixTime(string key) =>
        TryGetValue(key, out var raw)
        && long.TryParse(raw, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>
    /// Gets an absolute time from a field whose unit is not consistent across commands.
    /// </summary>
    /// <param name="key">The field name.</param>
    /// <returns>The time, or <see langword="null"/> when the field is absent, empty or zero.</returns>
    /// <remarks>
    /// Measured on 6.0.0-beta12.1 for the same file: <c>ftgetfilelist</c> writes <c>datetime</c> in
    /// milliseconds, <c>ftgetfileinfo</c> in nanoseconds, and the reference shows seconds. The unit is
    /// therefore read from the magnitude: any real date since 1973 in one unit is at least a thousand
    /// times larger than the same date in the next coarser unit.
    /// </remarks>
    public DateTimeOffset? GetUnixTimeOfAnyPrecision(string key)
    {
        if (!TryGetValue(key, out var raw)
            || !long.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            return null;
        }

        var ticks = value switch
        {
            < 100_000_000_000L => value * TimeSpan.TicksPerSecond,
            < 100_000_000_000_000L => value * TimeSpan.TicksPerMillisecond,
            < 100_000_000_000_000_000L => value * TimeSpan.TicksPerMicrosecond,
            _ => value / TimeSpan.NanosecondsPerTick,
        };

        return DateTimeOffset.UnixEpoch.AddTicks(ticks);
    }

    /// <summary>Gets a duration the server encodes as a whole number of seconds.</summary>
    /// <param name="key">The field name.</param>
    /// <returns>The duration, or <see langword="null"/> when the field is absent or not a number.</returns>
    public TimeSpan? GetDuration(string key) =>
        TryGetValue(key, out var raw) && long.TryParse(raw, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>
    /// Gets a required field, failing loudly when the server did not send it.
    /// </summary>
    /// <param name="key">The field name.</param>
    /// <returns>The field value.</returns>
    /// <exception cref="QueryProtocolException">
    /// Thrown when the field is absent, which means the response did not have the shape the caller
    /// expected.
    /// </exception>
    public string GetRequired(string key) =>
        TryGetValue(key, out var value)
            ? value
            : throw new QueryProtocolException(
                $"The response record has no '{key}' field. Present fields: {string.Join(", ", Keys)}.");
}