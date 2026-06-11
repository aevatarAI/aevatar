using Aevatar.CQRS.Core.Abstractions.Streaming;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.CQRS.Core.Tests;

// Test-add (test-coverage/cluster-035):
//   Covers refactor-introduced behavior in EventSinkProjectionLeaseOrchestrator.cs:41-144.
//   Cluster intent: live sink subscription handles are explicit caller-owned leases, not process registries.
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
    public async Task EnsureAndAttachLeaseAsync_WhenEnsureThrows_ShouldDisposeSinkAndRethrow()
    {
        var sink = new TrackingEventSink();
        var attachCalls = 0;
        var releaseCalls = 0;

        Func<Task> act = () => EventSinkProjectionLeaseOrchestrator.EnsureAndAttachLeaseAsync<TestLease, string>(
            _ => Task.FromException<TestLease?>(new InvalidOperationException("ensure failed")),
            (_, _, _) =>
            {
                attachCalls++;
                return Task.FromResult<IAsyncDisposable?>(null);
            },
            (_, _) =>
            {
                releaseCalls++;
                return Task.CompletedTask;
            },
            sink,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("ensure failed");
        attachCalls.Should().Be(0);
        releaseCalls.Should().Be(0);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task EnsureAndAttachLeaseAsync_WhenReleaseCleanupThrows_ShouldStillDisposeSinkAndRethrowAttachFailure()
    {
        var sink = new TrackingEventSink();
        var lease = new TestLease("lease-cleanup");
        var releaseCalls = 0;

        Func<Task> act = () => EventSinkProjectionLeaseOrchestrator.EnsureAndAttachLeaseAsync<TestLease, string>(
            _ => Task.FromResult<TestLease?>(lease),
            (_, _, _) => Task.FromException<IAsyncDisposable?>(new InvalidOperationException("attach failed")),
            (_, _) =>
            {
                releaseCalls++;
                throw new InvalidOperationException("release cleanup failed");
            },
            sink,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("attach failed");
        releaseCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task EnsureAndAttachLeaseAsync_WhenCanceledBeforeEnsure_ShouldThrowWithoutDisposingSink()
    {
        var sink = new TrackingEventSink();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => EventSinkProjectionLeaseOrchestrator.EnsureAndAttachLeaseAsync<TestLease, string>(
            _ => Task.FromResult<TestLease?>(new TestLease("lease-canceled")),
            (_, _, _) => Task.FromResult<IAsyncDisposable?>(null),
            (_, _) => Task.CompletedTask,
            sink,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.DisposeCalls.Should().Be(0);
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

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenLeaseIsNull_ShouldSkipDetachAndReleaseButCloseSink()
    {
        var sink = new TrackingEventSink();
        var detachCalls = 0;
        var releaseCalls = 0;

        await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync<TestLease, string>(
            null,
            new TrackingAsyncDisposable(),
            sink,
            (_, _) =>
            {
                detachCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                releaseCalls++;
                return Task.CompletedTask;
            },
            ct: CancellationToken.None);

        detachCalls.Should().Be(0);
        releaseCalls.Should().Be(0);
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenLeaseIsNull_ShouldStillRunOnDetachedAndCloseSink()
    {
        var sink = new TrackingEventSink();
        var sequence = new List<string>();

        await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync<TestLease, string>(
            null,
            null,
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

        sequence.Should().Equal("onDetached");
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenDetachThrows_ShouldStillReleaseCloseSinkAndRethrowDetachFailure()
    {
        var sink = new TrackingEventSink();
        var lease = new TestLease("lease-4");
        var sequence = new List<string>();

        var act = async () => await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            lease,
            new TrackingAsyncDisposable(),
            sink,
            (_, _) =>
            {
                sequence.Add("detach");
                throw new InvalidOperationException("detach failed");
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

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("detach failed");
        sequence.Should().Equal("detach", "onDetached", "release");
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenOnDetachedThrows_ShouldStillReleaseCloseSinkAndRethrowCallbackFailure()
    {
        var sink = new TrackingEventSink();
        var lease = new TestLease("lease-on-detached");
        var sequence = new List<string>();

        var act = async () => await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            lease,
            null,
            sink,
            (liveSinkLease, _) =>
            {
                liveSinkLease.Should().BeNull();
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
                throw new InvalidOperationException("on detached failed");
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("on detached failed");
        sequence.Should().Equal("detach", "onDetached", "release");
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenReleaseThrows_ShouldStillCloseSinkAndRethrowReleaseFailure()
    {
        var sink = new TrackingEventSink();
        var lease = new TestLease("lease-release");
        var sequence = new List<string>();

        var act = async () => await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
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
                throw new InvalidOperationException("release failed");
            },
            () =>
            {
                sequence.Add("onDetached");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("release failed");
        sequence.Should().Equal("detach", "onDetached", "release");
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenSinkCompleteThrows_ShouldStillDisposeAndRethrowCompleteFailure()
    {
        var sink = new TrackingEventSink
        {
            CompleteException = new InvalidOperationException("complete failed"),
        };
        var lease = new TestLease("lease-5");

        var act = async () => await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            lease,
            new TrackingAsyncDisposable(),
            sink,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("complete failed");
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenSinkDisposeThrows_ShouldRethrowDisposeFailure()
    {
        var sink = new TrackingEventSink
        {
            DisposeException = new InvalidOperationException("dispose failed"),
        };
        var lease = new TestLease("lease-dispose");

        var act = async () => await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            lease,
            new TrackingAsyncDisposable(),
            sink,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispose failed");
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenMultipleCleanupStepsThrow_ShouldRethrowFirstFailure()
    {
        var sink = new TrackingEventSink
        {
            CompleteException = new InvalidOperationException("complete failed"),
            DisposeException = new InvalidOperationException("dispose failed"),
        };
        var lease = new TestLease("lease-first-failure");

        var act = async () => await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            lease,
            new TrackingAsyncDisposable(),
            sink,
            (_, _) => throw new InvalidOperationException("detach failed"),
            (_, _) => throw new InvalidOperationException("release failed"),
            () => throw new InvalidOperationException("on detached failed"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("detach failed");
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_WhenCanceledBeforeDetach_ShouldThrowWithoutClosingSink()
    {
        var sink = new TrackingEventSink();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            new TestLease("lease-canceled"),
            new TrackingAsyncDisposable(),
            sink,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.CompleteCalls.Should().Be(0);
        sink.DisposeCalls.Should().Be(0);
    }

    private sealed record TestLease(string Id);

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingEventSink : IEventSink<string>
    {
        public int CompleteCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public Exception? CompleteException { get; init; }
        public Exception? DisposeException { get; init; }

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
            if (CompleteException != null)
                throw CompleteException;
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
            if (DisposeException != null)
                throw DisposeException;

            return ValueTask.CompletedTask;
        }
    }
}
