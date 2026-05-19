using Aevatar.CQRS.Core.Abstractions.Streaming;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.CQRS.Core.Tests;

public sealed class EventSinkProjectionLeaseOrchestratorTests
{
    [Fact]
    public async Task EnsureAndAttachLeaseAsync_WhenLeaseResolved_ShouldAttachAndReturnAttachment()
    {
        var sink = new TrackingEventSink();
        var lease = new TestLease("lease-1");
        var attachCalls = 0;

        var liveSinkLease = new TrackingAsyncDisposable();
        var attachment = await EventSinkProjectionLeaseOrchestrator.EnsureAndAttachLeaseAsync<TestLease, string>(
            _ => Task.FromResult<TestLease?>(lease),
            (runtimeLease, eventSink, _) =>
            {
                runtimeLease.Id.Should().Be("lease-1");
                eventSink.Should().BeSameAs(sink);
                attachCalls++;
                return Task.FromResult<IAsyncDisposable?>(liveSinkLease);
            },
            (_, _) => Task.CompletedTask,
            sink,
            CancellationToken.None);

        attachment.Should().NotBeNull();
        attachment!.ProjectionLease.Should().BeSameAs(lease);
        attachment.LiveSinkLease.Should().BeSameAs(liveSinkLease);
        attachCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAndAttachLeaseAsync_WhenLeaseIsNull_ShouldDisposeSinkAndReturnNull()
    {
        var sink = new TrackingEventSink();

        var attachment = await EventSinkProjectionLeaseOrchestrator.EnsureAndAttachLeaseAsync<TestLease, string>(
            _ => Task.FromResult<TestLease?>(null),
            (_, _, _) => Task.FromResult<IAsyncDisposable?>(null),
            (_, _) => Task.CompletedTask,
            sink,
            CancellationToken.None);

        attachment.Should().BeNull();
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task EnsureAndAttachLeaseAsync_WhenAttachThrows_ShouldReleaseAndDisposeThenRethrow()
    {
        var sink = new TrackingEventSink();
        var lease = new TestLease("lease-2");
        var releaseCalls = 0;

        Func<Task> act = () => EventSinkProjectionLeaseOrchestrator.EnsureAndAttachLeaseAsync<TestLease, string>(
            _ => Task.FromResult<TestLease?>(lease),
            (_, _, _) => Task.FromException<IAsyncDisposable?>(new InvalidOperationException("attach failed")),
            (_, _) =>
            {
                releaseCalls++;
                return Task.CompletedTask;
            },
            sink,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("attach failed");
        releaseCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_ShouldRunCleanupSequence()
    {
        var sink = new TrackingEventSink();
        var lease = new TestLease("lease-3");
        var sequence = new List<string>();

        await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            lease,
            new TrackingAsyncDisposable(),
            sink,
            (_, _) =>
            {
                sequence.Add("detach");
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                sequence.Add("release");
                return Task.CompletedTask;
            },
            () =>
            {
                sequence.Add("onDetached");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        sequence.Should().Equal("detach", "onDetached", "release");
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    private sealed record TestLease(string Id);

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingEventSink : IEventSink<string>
    {
        public int CompleteCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Push(string evt)
        {
            _ = evt;
        }

        public ValueTask PushAsync(string evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
            CompleteCalls++;
        }

        public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
