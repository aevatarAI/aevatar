using Aevatar.CQRS.Core.Abstractions.Streaming;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.CQRS.Core.Tests;

public sealed class EventSinkProjectionLifecyclePortExtensionsTests
{
    [Fact]
    public async Task EnsureAndAttachAsync_WhenLeaseResolved_ShouldAttachViaLifecyclePort()
    {
        var sink = new TrackingEventSink();
        var lifecyclePort = new TrackingLifecyclePort();
        var lease = new TestLease("lease-1");

        var resolved = await lifecyclePort.EnsureAndAttachAsync(
            _ => Task.FromResult<TestLease?>(lease),
            sink,
            CancellationToken.None);

        resolved.Should().BeSameAs(lease);
        lifecyclePort.AttachCalls.Should().Be(1);
        lifecyclePort.ReleaseCalls.Should().Be(0);
        sink.DisposeCalls.Should().Be(0);
    }

    [Fact]
    public async Task EnsureAndAttachAsync_WhenLeaseIsNull_ShouldDisposeSink()
    {
        var sink = new TrackingEventSink();
        var lifecyclePort = new TrackingLifecyclePort();

        var resolved = await lifecyclePort.EnsureAndAttachAsync(
            _ => Task.FromResult<TestLease?>(null),
            sink,
            CancellationToken.None);

        resolved.Should().BeNull();
        lifecyclePort.AttachCalls.Should().Be(0);
        lifecyclePort.ReleaseCalls.Should().Be(0);
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachReleaseAndDisposeAsync_ShouldDelegateCleanupViaLifecyclePort()
    {
        var sink = new TrackingEventSink();
        var lifecyclePort = new TrackingLifecyclePort();
        var lease = new TestLease("lease-2");
        var onDetachedCalls = 0;

        await lifecyclePort.DetachReleaseAndDisposeAsync(
            lease,
            sink,
            () =>
            {
                onDetachedCalls++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        lifecyclePort.DetachCalls.Should().Be(1);
        lifecyclePort.ReleaseCalls.Should().Be(1);
        onDetachedCalls.Should().Be(1);
        sink.CompleteCalls.Should().Be(1);
        sink.DisposeCalls.Should().Be(1);
    }

    private sealed record TestLease(string Id);

    private sealed class TrackingLifecyclePort : IEventSinkProjectionLifecyclePort<TestLease, string>
    {
        public int AttachCalls { get; private set; }
        public int DetachCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public bool ProjectionEnabled => true;

        public Task AttachLiveSinkAsync(TestLease lease, IEventSink<string> sink, CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            AttachCalls++;
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(TestLease lease, IEventSink<string> sink, CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            DetachCalls++;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(TestLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            ReleaseCalls++;
            return Task.CompletedTask;
        }
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
