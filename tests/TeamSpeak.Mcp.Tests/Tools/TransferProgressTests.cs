using ModelContextProtocol;

using TeamSpeak.Mcp.Tools;

namespace TeamSpeak.Mcp.Tests.Tools;

/// <summary>How byte counts from a transfer become progress notifications.</summary>
public class TransferProgressTests
{
    private sealed class Recorder : IProgress<ProgressNotificationValue>
    {
        public List<ProgressNotificationValue> Values { get; } = [];

        public void Report(ProgressNotificationValue value) => Values.Add(value);
    }

    [Fact]
    public void Chunks_in_quick_succession_are_reported_once_and_the_end_always()
    {
        var recorder = new Recorder();
        var progress = new TransferProgress(recorder, 0, 1_000_000, "Uploaded");

        for (var done = 80_000L; done < 1_000_000; done += 80_000)
        {
            progress.Report(done);
        }

        progress.Report(1_000_000);

        // Chunks arrive faster than the interval, so only the finish gets through.
        var last = Assert.Single(recorder.Values);
        Assert.Equal(1_000_000, last.Progress);
        Assert.Equal(1_000_000, last.Total);
        Assert.Equal("Uploaded 1000000 of 1000000 bytes", last.Message);
    }

    [Fact]
    public void A_resumed_transfer_counts_the_bytes_already_in_place()
    {
        var recorder = new Recorder();
        var progress = new TransferProgress(recorder, 400, 1_000, "Downloaded");

        progress.Report(600);

        Assert.Equal(1_000, Assert.Single(recorder.Values).Progress);
    }

    [Fact]
    public void Without_a_listener_nothing_is_reported()
    {
        new TransferProgress(null, 0, 10, "Uploaded").Report(10);
    }
}