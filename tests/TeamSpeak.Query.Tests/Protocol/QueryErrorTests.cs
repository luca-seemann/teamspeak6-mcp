using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.Protocol;

public class QueryErrorTests
{
    [Fact]
    public void IsSuccess_is_true_only_for_error_id_zero()
    {
        Assert.True(new QueryError(0, "ok").IsSuccess);
        Assert.False(new QueryError(1281, "database empty result set").IsSuccess);
    }
}