using System.Runtime.CompilerServices;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class GAgentDraftRunProjectionInfrastructureTests
{
    [Fact]
    public void SessionEventCodec_ShouldSerializeDeserializeAndValidateEventType()
    {
        var codec = new GAgentDraftRunSessionEventCodec();
        var evt = new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "actor-1",
                RunId = "cmd-1",
            },
        };

        codec.Channel.Should().Be("gagent-draft-run");
        codec.GetEventType(evt).Should().Be(AGUIEvent.EventOneofCase.RunFinished.ToString());

        var payload = codec.Serialize(evt);
        codec.Deserialize(codec.GetEventType(evt), payload).Should().BeEquivalentTo(evt);
        codec.Deserialize("DifferentType", payload).Should().BeNull();
        codec.Deserialize("", payload).Should().BeNull();
        codec.Deserialize(codec.GetEventType(evt), ByteString.Empty).Should().BeNull();
        codec.Deserialize(codec.GetEventType(evt), ByteString.CopyFromUtf8("not-a-proto")).Should().BeNull();
        codec.GetEventType(new AGUIEvent()).Should().Be(AGUIEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task ProjectionPort_ShouldStartAttachDetachAndReleaseDraftRunSession()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var port = new GAgentDraftRunProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            activation,
            release,
            hub);
        var lease = await port.EnsureActorProjectionAsync("actor-1", "cmd-1", CancellationToken.None);
        var sink = new RecordingEventSink();

        lease.Should().BeSameAs(activation.LeaseToReturn);
        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("actor-1");
        activation.Requests[0].ProjectionKind.Should().Be("service-draft-run-session");
        activation.Requests[0].SessionId.Should().Be("cmd-1");

        await port.AttachLiveSinkAsync(lease!, sink, CancellationToken.None);
        await hub.Handler!(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "actor-1",
                RunId = "cmd-1",
            },
        });
        await port.DetachLiveSinkAsync(lease, sink, CancellationToken.None);
        await port.ReleaseActorProjectionAsync(lease, CancellationToken.None);

        hub.SubscribeCalls.Should().Be(1);
        hub.LastScopeId.Should().Be("actor-1");
        hub.LastSessionId.Should().Be("cmd-1");
        sink.Events.Should().ContainSingle();
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    private sealed class RecordingActivationService : IProjectionScopeActivationService<GAgentDraftRunRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public GAgentDraftRunRuntimeLease LeaseToReturn { get; } = new(new GAgentDraftRunProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "service-draft-run-session",
            SessionId = "cmd-1",
        });

        public Task<GAgentDraftRunRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(LeaseToReturn);
        }
    }

    private sealed class RecordingReleaseService : IProjectionScopeReleaseService<GAgentDraftRunRuntimeLease>
    {
        public List<GAgentDraftRunRuntimeLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(GAgentDraftRunRuntimeLease lease, CancellationToken ct = default)
        {
            Leases.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionEventHub : IProjectionSessionEventHub<AGUIEvent>
    {
        public int SubscribeCalls { get; private set; }
        public string? LastScopeId { get; private set; }
        public string? LastSessionId { get; private set; }
        public Func<AGUIEvent, ValueTask>? Handler { get; private set; }

        public Task PublishAsync(string scopeId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = evt;
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<AGUIEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            SubscribeCalls++;
            LastScopeId = scopeId;
            LastSessionId = sessionId;
            Handler = handler;
            return Task.FromResult<IAsyncDisposable>(new NoopSubscription());
        }
    }

    private sealed class RecordingEventSink : IEventSink<AGUIEvent>
    {
        public List<AGUIEvent> Events { get; } = [];

        public void Push(AGUIEvent evt) => Events.Add(evt);

        public ValueTask PushAsync(AGUIEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<AGUIEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopSubscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
