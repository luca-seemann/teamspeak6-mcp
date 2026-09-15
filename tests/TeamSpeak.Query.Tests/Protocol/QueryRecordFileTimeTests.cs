using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

/// <summary>File timestamps, whose unit differs between commands.</summary>
public class QueryRecordFileTimeTests
{
    private static DateTimeOffset? Time(string value) =>
        new QueryRecord(new Dictionary<string, string> { ["datetime"] = value }).GetUnixTimeOfAnyPrecision("datetime");

    [Fact]
    public void Reads_the_same_moment_in_seconds_milliseconds_and_nanoseconds()
    {
        // The same file on 6.0.0-beta12.1: ftgetfilelist wrote milliseconds, ftgetfileinfo nanoseconds.
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1789427150856);

        Assert.Equal(expected, Time("1789427150856"));
        Assert.Equal(expected.AddTicks(6774), Time("1789427150856677454"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789427150), Time("1789427150"));
        Assert.Equal(expected, Time("1789427150856000"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("never")]
    public void Reads_nothing_from_a_zero_or_unreadable_value(string value) =>
        Assert.Null(Time(value));
}