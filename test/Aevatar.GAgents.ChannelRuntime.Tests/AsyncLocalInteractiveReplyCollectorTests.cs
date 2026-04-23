using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AsyncLocalInteractiveReplyCollectorTests
{
    [Fact]
    public void TryTake_returns_null_without_scope()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();

        collector.TryTake().Should().BeNull();
    }

    [Fact]
    public void Capture_without_scope_is_silently_dropped()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();

        collector.Capture(new MessageContent { Text = "hello" });

        collector.TryTake().Should().BeNull();
    }

    [Fact]
    public void Capture_inside_scope_is_taken_once()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        using var scope = collector.BeginScope();

        collector.Capture(new MessageContent { Text = "hello" });

        var first = collector.TryTake();
        first.Should().NotBeNull();
        first!.Text.Should().Be("hello");

        collector.TryTake().Should().BeNull();
    }

    [Fact]
    public void Capture_overwrites_previous_intent_within_same_scope()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        using var scope = collector.BeginScope();
        collector.Capture(new MessageContent { Text = "first" });
        collector.Capture(new MessageContent { Text = "second" });

        var taken = collector.TryTake();
        taken.Should().NotBeNull();
        taken!.Text.Should().Be("second");
    }

    [Fact]
    public async Task Scopes_are_isolated_across_concurrent_flows()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();

        var task1 = Task.Run(() =>
        {
            using var scope = collector.BeginScope();
            collector.Capture(new MessageContent { Text = "flow-1" });
            return collector.TryTake()?.Text;
        });

        var task2 = Task.Run(() =>
        {
            using var scope = collector.BeginScope();
            collector.Capture(new MessageContent { Text = "flow-2" });
            return collector.TryTake()?.Text;
        });

        var results = await Task.WhenAll(task1, task2);

        results.Should().BeEquivalentTo(new[] { "flow-1", "flow-2" });
    }

    [Fact]
    public void Disposed_scope_removes_capture_slot()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var scope = collector.BeginScope();
        collector.Capture(new MessageContent { Text = "hello" });
        scope.Dispose();

        collector.TryTake().Should().BeNull();
    }
}
