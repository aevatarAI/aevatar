using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using System.Runtime.CompilerServices;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class LlmSessionObservationProjectionPortTests
{
    [Fact]
    public void Constructor_NullAttachExistingLeaseLookup_Throws()
    {
        var act = () => new LlmSessionObservationProjectionPort(
            new ServiceProjectionOptions(),
            new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>(),
            CreateSessionEventHub(),
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("attachExistingLeaseLookup");
    }

    [Fact]
    public async Task AttachExistingResponseProjectionAsync_WhenProjectionDisabled_ReturnsNullWithoutLookup()
    {
        var lookup = new RecordingAttachExistingLeaseLookup();
        var port = CreatePort(lookup, enabled: false);

        var attachment = await port.AttachExistingResponseProjectionAsync(
            "actor-1",
            "response-1",
            new RecordingEventSink());

        attachment.Should().BeNull();
        lookup.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "response-1")]
    [InlineData("  ", "response-1")]
    [InlineData("actor-1", "")]
    [InlineData("actor-1", "  ")]
    public async Task AttachExistingResponseProjectionAsync_BlankIdentifiers_ReturnsNullWithoutLookup(
        string actorId,
        string responseId)
    {
        var lookup = new RecordingAttachExistingLeaseLookup();
        var port = CreatePort(lookup);

        var attachment = await port.AttachExistingResponseProjectionAsync(
            actorId,
            responseId,
            new RecordingEventSink());

        attachment.Should().BeNull();
        lookup.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AttachExistingResponseProjectionAsync_NullSink_Throws()
    {
        var port = CreatePort(new RecordingAttachExistingLeaseLookup());

        var act = async () => await port.AttachExistingResponseProjectionAsync(
            "actor-1",
            "response-1",
            null!);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("sink");
    }

    [Fact]
    public async Task AttachExistingResponseProjectionAsync_WhenCancelled_Throws()
    {
        var port = CreatePort(new RecordingAttachExistingLeaseLookup());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await port.AttachExistingResponseProjectionAsync(
            "actor-1",
            "response-1",
            new RecordingEventSink(),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AttachExistingResponseProjectionAsync_WhenLeaseMissing_ReturnsNull()
    {
        var lookup = new RecordingAttachExistingLeaseLookup();
        var port = CreatePort(lookup);

        var attachment = await port.AttachExistingResponseProjectionAsync(
            "  actor-1  ",
            "  response-1  ",
            new RecordingEventSink());

        attachment.Should().BeNull();
        lookup.Requests.Should().ContainSingle();
        var request = lookup.Requests[0];
        request.RootActorId.Should().Be("actor-1");
        request.SessionId.Should().Be("response-1");
        request.ProjectionKind.Should().Be("llm-session-observation");
        request.Mode.Should().Be(ProjectionRuntimeMode.SessionObservation);
    }

    [Fact]
    public async Task AttachExistingResponseProjectionAsync_WithExistingLease_AttachesSinkAndReleasesLease()
    {
        var streams = new InMemoryStreamProvider();
        var hub = CreateSessionEventHub(streams);
        var release = new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>();
        var lease = new LlmSessionObservationRuntimeLease(new LlmSessionObservationProjectionContext
        {
            RootActorId = "actor-9",
            ProjectionKind = "llm-session-observation",
            SessionId = "response-9",
        });
        var lookup = new RecordingAttachExistingLeaseLookup { Lease = lease };
        var port = CreatePort(lookup, release, hub);
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingResponseProjectionAsync(
            " actor-9 ",
            " response-9 ",
            sink);

        attachment.Should().NotBeNull();
        attachment!.ProjectionLease.Should().BeSameAs(lease);
        attachment.LiveSinkLease.Should().NotBeNull();

        var envelope = new EventEnvelope
        {
            Id = "evt-llm-observation",
            Payload = Any.Pack(new StringValue { Value = "chunk" }),
        };
        await hub.PublishAsync("actor-9", "response-9", envelope);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sink.WaitForCountAsync(1, cts.Token);
        sink.PushedEvents.Should().ContainSingle().Which.Should().Be(envelope);

        await port.DetachLiveSinkAsync(attachment.LiveSinkLease);
        await port.ReleaseActorProjectionAsync(lease);

        release.Released.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    private static LlmSessionObservationProjectionPort CreatePort(
        RecordingAttachExistingLeaseLookup lookup,
        bool enabled = true) =>
        CreatePort(
            lookup,
            new RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease>(),
            CreateSessionEventHub(),
            enabled);

    private static LlmSessionObservationProjectionPort CreatePort(
        RecordingAttachExistingLeaseLookup lookup,
        RecordingProjectionReleaseService<LlmSessionObservationRuntimeLease> release,
        LlmSessionObservationSessionEventHub sessionEventHub,
        bool enabled = true) =>
        new(
            new ServiceProjectionOptions { Enabled = enabled },
            release,
            sessionEventHub,
            lookup);

    private static LlmSessionObservationSessionEventHub CreateSessionEventHub(
        InMemoryStreamProvider? streams = null) =>
        new(streams ?? new InMemoryStreamProvider(), new LlmSessionObservationSessionEventCodec());

    private sealed class RecordingAttachExistingLeaseLookup
        : IProjectionScopeAttachExistingLeaseLookup<LlmSessionObservationRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public LlmSessionObservationRuntimeLease? Lease { get; init; }

        public Task<LlmSessionObservationRuntimeLease?> TryGetAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Lease);
        }
    }

    private sealed class RecordingEventSink : IEventSink<EventEnvelope>
    {
        private readonly Lock _lock = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];

        public List<EventEnvelope> PushedEvents { get; } = [];

        public void Push(EventEnvelope evt)
        {
            lock (_lock)
            {
                PushedEvents.Add(evt);
                CompleteSatisfiedWaitersLocked();
            }
        }

        public ValueTask PushAsync(EventEnvelope evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Push(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public Task WaitForCountAsync(int count, CancellationToken ct)
        {
            lock (_lock)
            {
                if (PushedEvents.Count >= count)
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _countWaiters.Add((count, signal));
                ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), signal);
                return signal.Task;
            }
        }

        public async IAsyncEnumerable<EventEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void CompleteSatisfiedWaitersLocked()
        {
            foreach (var waiter in _countWaiters.Where(x => PushedEvents.Count >= x.Count).ToArray())
            {
                waiter.Signal.TrySetResult(true);
                _countWaiters.Remove(waiter);
            }
        }
    }
}
