using System.Runtime.CompilerServices;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

// Test-add (test-coverage/pr-678/cluster-003):
//   Covers refactor-introduced behavior in ScriptServiceAguiProjectionPort and ScriptServiceAguiRuntimeLease.
//   Cluster intent: ScopeService AGUI streams moved from host-local pumping into the Projection Pipeline.
public sealed class ScriptServiceAguiProjectionPortTests
{
    private const string ScriptServiceAguiProjectionKind = "script-service-agui-session";

    [Fact]
    public void SessionEventCodec_ShouldSerializeDeserializeAndValidateEventType()
    {
        var codec = new ScriptServiceAguiSessionEventCodec();
        var evt = new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "script-actor-1",
                RunId = "run-1",
            },
        };

        codec.Channel.Should().Be(ScriptServiceAguiProjectionKind);
        codec.GetEventType(evt).Should().Be(AGUIEvent.EventOneofCase.RunFinished.ToString());

        var payload = codec.Serialize(evt);
        codec.Deserialize(codec.GetEventType(evt), payload).Should().BeEquivalentTo(evt);
        codec.Deserialize("DifferentType", payload).Should().BeNull();
        codec.Deserialize("", payload).Should().BeNull();
        codec.Deserialize(codec.GetEventType(evt), null!).Should().BeNull();
        codec.Deserialize(codec.GetEventType(evt), ByteString.Empty).Should().BeNull();
        codec.Deserialize(codec.GetEventType(evt), ByteString.CopyFromUtf8("not-a-proto")).Should().BeNull();
        codec.GetEventType(new AGUIEvent()).Should().Be(AGUIEvent.Descriptor.FullName);

        var getEventTypeWithNull = () => codec.GetEventType(null!);
        getEventTypeWithNull.Should().Throw<ArgumentNullException>();
        var serializeWithNull = () => codec.Serialize(null!);
        serializeWithNull.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldResolveChannelScopedAguiPortsAndProjectors()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();
        services.AddSingleton<IStreamProvider, InMemoryStreamProvider>();
        services.AddSingleton<IProjectionScopeActivationService<GAgentDraftRunRuntimeLease>>(
            new StaticActivationService<GAgentDraftRunRuntimeLease>(new GAgentDraftRunRuntimeLease(new GAgentDraftRunProjectionContext
            {
                RootActorId = "draft-actor-1",
                ProjectionKind = "service-draft-run-session",
                SessionId = "draft-run-1",
            })));
        services.AddSingleton<IProjectionScopeReleaseService<GAgentDraftRunRuntimeLease>, StaticReleaseService<GAgentDraftRunRuntimeLease>>();
        services.AddSingleton<IProjectionScopeActivationService<ScriptServiceAguiRuntimeLease>>(
            new StaticActivationService<ScriptServiceAguiRuntimeLease>(new ScriptServiceAguiRuntimeLease(new ScriptServiceAguiProjectionContext
            {
                RootActorId = "script-actor-1",
                ProjectionKind = ScriptServiceAguiProjectionKind,
                SessionId = "run-1",
            })));
        services.AddSingleton<IProjectionScopeReleaseService<ScriptServiceAguiRuntimeLease>, StaticReleaseService<ScriptServiceAguiRuntimeLease>>();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IGAgentDraftRunProjectionPort>()
            .Should().BeOfType<GAgentDraftRunProjectionPort>();
        provider.GetRequiredService<IScriptServiceAguiProjectionPort>()
            .Should().BeOfType<ScriptServiceAguiProjectionPort>();
        provider.GetServices<IProjectionProjector<GAgentDraftRunProjectionContext>>()
            .Should().ContainSingle(x => x is GAgentDraftRunSessionEventProjector);
        provider.GetServices<IProjectionProjector<ScriptServiceAguiProjectionContext>>()
            .Should().ContainSingle(x => x is ScriptServiceAguiSessionEventProjector);
        provider.GetServices<IProjectionSessionEventCodec<AGUIEvent>>()
            .Should().BeEmpty("AGUIEvent codecs are channel-scoped inside each pipeline factory");
        provider.GetServices<IProjectionSessionEventHub<AGUIEvent>>()
            .Should().BeEmpty("AGUIEvent hubs are channel-scoped inside each pipeline factory");
    }

    [Fact]
    public async Task EnsureAttachDetachRelease_ShouldUseSessionProjectionLease()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var port = new ScriptServiceAguiProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            activation,
            release,
            hub);
        var sink = new RecordingEventSink();

        var lease = await port.EnsureRunProjectionAsync("script-actor-1", "run-1", CancellationToken.None);
        await port.AttachLiveSinkAsync(lease!, sink, CancellationToken.None);
        await hub.Handler!(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "script-actor-1",
                RunId = "run-1",
            },
        });
        await port.DetachLiveSinkAsync(lease!, sink, CancellationToken.None);
        await port.ReleaseActorProjectionAsync(lease!, CancellationToken.None);

        var request = activation.Requests.Should().ContainSingle().Subject;
        request.RootActorId.Should().Be("script-actor-1");
        request.SessionId.Should().Be("run-1");
        request.ProjectionKind.Should().Be(ScriptServiceAguiProjectionKind);
        request.Mode.Should().Be(ProjectionRuntimeMode.SessionObservation);

        var runtimeLease = lease.Should().BeOfType<ScriptServiceAguiRuntimeLease>().Subject;
        runtimeLease.ActorId.Should().Be("script-actor-1");
        runtimeLease.RootEntityId.Should().Be("script-actor-1");
        runtimeLease.RunId.Should().Be("run-1");
        runtimeLease.SessionId.Should().Be("run-1");
        runtimeLease.ScopeId.Should().Be("script-actor-1");
        runtimeLease.Context.Should().BeSameAs(activation.LeaseToReturn.Context);

        hub.SubscribeCalls.Should().Be(1);
        hub.LastScopeId.Should().Be("script-actor-1");
        hub.LastSessionId.Should().Be("run-1");
        sink.Events.Should().ContainSingle().Which.RunFinished.RunId.Should().Be("run-1");
        hub.DisposedSubscriptions.Should().Be(1);
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    [Fact]
    public async Task DisabledProjection_ShouldNotActivateAttachOrRelease()
    {
        var activation = new RecordingActivationService();
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var port = new ScriptServiceAguiProjectionPort(
            new ServiceProjectionOptions { Enabled = false },
            activation,
            release,
            hub);

        var lease = await port.EnsureRunProjectionAsync("script-actor-1", "run-1", CancellationToken.None);
        await port.AttachLiveSinkAsync(new ScriptServiceAguiRuntimeLease(new ScriptServiceAguiProjectionContext
        {
            RootActorId = "script-actor-1",
            SessionId = "run-1",
            ProjectionKind = ScriptServiceAguiProjectionKind,
        }), new RecordingEventSink(), CancellationToken.None);
        await port.ReleaseActorProjectionAsync(new ScriptServiceAguiRuntimeLease(new ScriptServiceAguiProjectionContext
        {
            RootActorId = "script-actor-1",
            SessionId = "run-1",
            ProjectionKind = ScriptServiceAguiProjectionKind,
        }), CancellationToken.None);

        lease.Should().BeNull();
        activation.Requests.Should().BeEmpty();
        hub.SubscribeCalls.Should().Be(0);
        release.Leases.Should().BeEmpty();
    }

    private sealed class RecordingActivationService : IProjectionScopeActivationService<ScriptServiceAguiRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public ScriptServiceAguiRuntimeLease LeaseToReturn { get; } = new(new ScriptServiceAguiProjectionContext
        {
            RootActorId = "script-actor-1",
            ProjectionKind = ScriptServiceAguiProjectionKind,
            SessionId = "run-1",
        });

        public Task<ScriptServiceAguiRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(LeaseToReturn);
        }
    }

    private sealed class RecordingReleaseService : IProjectionScopeReleaseService<ScriptServiceAguiRuntimeLease>
    {
        public List<ScriptServiceAguiRuntimeLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(ScriptServiceAguiRuntimeLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Leases.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionEventHub : IProjectionSessionEventHub<AGUIEvent>
    {
        public int SubscribeCalls { get; private set; }
        public int DisposedSubscriptions { get; private set; }
        public string? LastScopeId { get; private set; }
        public string? LastSessionId { get; private set; }
        public Func<AGUIEvent, ValueTask>? Handler { get; private set; }

        public Task PublishAsync(string scopeId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<AGUIEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SubscribeCalls++;
            LastScopeId = scopeId;
            LastSessionId = sessionId;
            Handler = handler;
            return Task.FromResult<IAsyncDisposable>(new DelegateAsyncDisposable(() => DisposedSubscriptions++));
        }
    }

    private sealed class RecordingEventSink : IEventSink<AGUIEvent>
    {
        public List<AGUIEvent> Events { get; } = [];

        public void Push(AGUIEvent evt) => Events.Add(evt);

        public ValueTask PushAsync(AGUIEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<AGUIEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelegateAsyncDisposable(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticActivationService<TLease>(TLease lease) : IProjectionScopeActivationService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        public Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(lease);
        }
    }

    private sealed class StaticReleaseService<TLease> : IProjectionScopeReleaseService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        public Task ReleaseIfIdleAsync(TLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
