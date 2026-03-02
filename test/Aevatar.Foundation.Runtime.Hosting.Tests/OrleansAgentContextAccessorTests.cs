using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Context;
using FluentAssertions;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansAgentContextAccessorTests
{
    [Fact]
    public void Context_SetWithValues_ShouldExposeStringValues()
    {
        RequestContext.Clear();
        var accessor = new OrleansAgentContextAccessor();
        var source = new AsyncLocalAgentContext();
        source.Set("userId", 42);
        source.Set("traceId", "trace-1");

        accessor.Context = source;

        accessor.Context.Should().NotBeNull();
        accessor.Context!.Get<string>("userId").Should().Be("42");
        accessor.Context.Get<int>("userId").Should().Be(default);
        accessor.Context.Get<string>("traceId").Should().Be("trace-1");
    }

    [Fact]
    public void Context_SetNull_ShouldOnlyClearContextPrefixedKeys()
    {
        RequestContext.Clear();
        var accessor = new OrleansAgentContextAccessor();
        RequestContext.Set("non_ctx", "keep");
        var source = new AsyncLocalAgentContext();
        source.Set("k1", "v1");
        accessor.Context = source;

        accessor.Context = null;

        RequestContext.Get("non_ctx").Should().Be("keep");
        accessor.Context.Should().BeNull();
    }
}
