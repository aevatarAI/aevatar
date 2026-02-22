using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public class AsyncLocalAgentContextAccessorTests
{
    [Fact]
    public async Task Context_ShouldRoundtripAcrossAsyncBoundary()
    {
        var accessor = new AsyncLocalAgentContextAccessor();
        accessor.Context.Should().BeNull();

        var context = new AsyncLocalAgentContext();
        context.Set("trace", "trace-1");
        accessor.Context = context;

        await Task.Yield();
        accessor.Context.Should().BeSameAs(context);
        accessor.Context!.Get<string>("trace").Should().Be("trace-1");

        accessor.Context = null;
        accessor.Context.Should().BeNull();
    }
}
