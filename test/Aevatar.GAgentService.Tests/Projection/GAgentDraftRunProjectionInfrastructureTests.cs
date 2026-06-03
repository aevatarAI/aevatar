using System.Runtime.CompilerServices;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class GAgentDraftRunProjectionInfrastructureTests
{
    [Fact]
    public void AGUIEventDescriptor_ShouldKeepWireTypeIdentityStable()
    {
        AGUIEvent.Descriptor.FullName.Should().Be("aevatar.presentation.agui.AGUIEvent");
        Any.Pack(new AGUIEvent()).TypeUrl.Should().Be("type.googleapis.com/aevatar.presentation.agui.AGUIEvent");
    }

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
    public async Task ProjectionPort_ShouldAttachDetachAndReleaseExistingDraftRunSession()
    {
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.KnownActorIds.Add(ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            "actor-1",
            "service-draft-run-session",
            ProjectionRuntimeMode.SessionObservation,
            "cmd-1")));
        var port = new GAgentDraftRunProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            release,
            hub,
            CreateAttachExistingLookup(runtime));
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingActorProjectionAsync("actor-1", "cmd-1", sink, CancellationToken.None);
        var lease = attachment!.ProjectionLease;
        await hub.Handler!(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "actor-1",
                RunId = "cmd-1",
            },
        });
        await port.DetachLiveSinkAsync(attachment.LiveSinkLease, CancellationToken.None);
        await port.ReleaseActorProjectionAsync(lease, CancellationToken.None);
        hub.SubscribeCalls.Should().Be(1);
        hub.LastRootActorId.Should().Be("actor-1");
        hub.LastSessionId.Should().Be("cmd-1");
        sink.Events.Should().ContainSingle();
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    [Fact]
    public async Task ProjectionPort_ShouldAttachExistingDraftRunSession_WhenScopeActorExists()
    {
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.KnownActorIds.Add(ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            "actor-1",
            "service-draft-run-session",
            ProjectionRuntimeMode.SessionObservation,
            "cmd-1")));
        var port = new GAgentDraftRunProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingActorProjectionAsync(
            "actor-1",
            "cmd-1",
            sink,
            CancellationToken.None);

        attachment.Should().NotBeNull();
        var lease = attachment!.ProjectionLease.Should().BeOfType<GAgentDraftRunRuntimeLease>().Subject;
        lease.ActorId.Should().Be("actor-1");
        lease.CommandId.Should().Be("cmd-1");
        hub.SubscribeCalls.Should().Be(1);
        hub.LastRootActorId.Should().Be("actor-1");
        hub.LastSessionId.Should().Be("cmd-1");

        await hub.Handler!(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "actor-1",
                RunId = "cmd-1",
            },
        });
        sink.Events.Should().ContainSingle().Which.RunFinished.RunId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task ProjectionPort_ShouldReturnNullForAttachExisting_WhenScopeActorIsMissingOrInvalid()
    {
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.KnownActorIds.Add("different-scope");
        var disabledPort = new GAgentDraftRunProjectionPort(
            new ServiceProjectionOptions { Enabled = false },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
        var enabledPort = new GAgentDraftRunProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));

        (await disabledPort.AttachExistingActorProjectionAsync(
            "actor-1",
            "cmd-1",
            new RecordingEventSink(),
            CancellationToken.None)).Should().BeNull();
        (await enabledPort.AttachExistingActorProjectionAsync(
            "actor-1",
            "cmd-1",
            new RecordingEventSink(),
            CancellationToken.None)).Should().BeNull();
        (await enabledPort.AttachExistingActorProjectionAsync(
            "",
            "cmd-1",
            new RecordingEventSink(),
            CancellationToken.None)).Should().BeNull();
        (await enabledPort.AttachExistingActorProjectionAsync(
            "actor-1",
            " ",
            new RecordingEventSink(),
            CancellationToken.None)).Should().BeNull();
        hub.SubscribeCalls.Should().Be(0);
    }

    [Fact]
    public void ProjectionPort_ShouldValidateAttachExistingLookupDependency()
    {
        var create = () => new GAgentDraftRunProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            new RecordingSessionEventHub(),
            null!);

        create.Should().Throw<ArgumentNullException>().WithParameterName("attachExistingLeaseLookup");
    }

    private static IProjectionScopeAttachExistingLeaseLookup<GAgentDraftRunRuntimeLease> CreateAttachExistingLookup(
        IActorRuntime runtime) =>
        new ProjectionScopeAttachExistingLeaseLookup<GAgentDraftRunRuntimeLease, GAgentDraftRunProjectionContext>(
            runtime,
            static request => new GAgentDraftRunProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            },
            static (_, context) => new GAgentDraftRunRuntimeLease(context));

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
        public string? LastRootActorId { get; private set; }
        public string? LastSessionId { get; private set; }
        public Func<AGUIEvent, ValueTask>? Handler { get; private set; }

        public Task PublishAsync(string rootActorId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            _ = rootActorId;
            _ = sessionId;
            _ = evt;
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<AGUIEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            SubscribeCalls++;
            LastRootActorId = rootActorId;
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
