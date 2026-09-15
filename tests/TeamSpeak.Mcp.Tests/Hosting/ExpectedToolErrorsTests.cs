using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using TeamSpeak.Mcp.Hosting;

namespace TeamSpeak.Mcp.Tests.Hosting;

public class ExpectedToolErrorsTests
{
    private static ValueTask<CallToolResult> Invoke(Func<CallToolResult> tool)
    {
        // The filter never looks at the request, so none is needed to exercise it.
        var handler = ExpectedToolErrors.Filter((_, _) => ValueTask.FromResult(tool()));
        return handler(null!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Answers_an_expected_tool_error_with_an_error_result_carrying_its_message()
    {
        var result = await Invoke(() => throw new McpException("ts_channel_edit needs the Write safety level."));

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("ts_channel_edit needs the Write safety level.", text.Text);
    }

    [Fact]
    public async Task Passes_a_successful_result_through_untouched()
    {
        var expected = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };

        Assert.Same(expected, await Invoke(() => expected));
    }

    [Fact]
    public async Task Leaves_a_protocol_error_to_the_sdk() =>
        await Assert.ThrowsAsync<McpProtocolException>(async () => await Invoke(() => throw new McpProtocolException("unknown tool")));

    [Fact]
    public async Task Leaves_an_unexpected_exception_to_the_sdk_so_it_is_logged_in_full() =>
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Invoke(() => throw new InvalidOperationException("bug")));
}