using System.Runtime.CompilerServices;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.AGUI.Contracts;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

// Test-add (test-coverage/pr-678/cluster-003):
//   Covers refactor-introduced behavior in ScriptServiceAguiProjectionPort and ScriptServiceAguiRuntimeLease.
//   Cluster intent: ScopeService AGUI streams moved from host-local pumping into the Projection Pipeline.
public sealed class ScriptServiceAguiProjectionPortTests
{
    private const string ScriptServiceAguiProjectionKind = "script-service-agui-session";

    [Fact]
    public async Task AttachExistingDetachRelease_ShouldUseSessionProjectionLease()
    {
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.KnownActorIds.Add(BuildScopeActorId(
            "script-actor-1",
            ScriptServiceAguiProjectionKind,
            ProjectionRuntimeMode.SessionObservation,
            "run-1"));
        var port = new ScriptServiceAguiProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            release,
            hub,
            CreateAttachExistingLookup(runtime));
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingRunProjectionAsync("script-actor-1", "run-1", sink, CancellationToken.None);
        await hub.Handler!(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "script-actor-1",
                RunId = "run-1",
            },
        });
        await port.DetachLiveSinkAsync(attachment!.LiveSinkLease, CancellationToken.None);
        await port.ReleaseActorProjectionAsync(attachment.ProjectionLease, CancellationToken.None);
        var runtimeLease = attachment.ProjectionLease.Should().BeOfType<ScriptServiceAguiRuntimeLease>().Subject;
        runtimeLease.ActorId.Should().Be("script-actor-1");
        runtimeLease.RootEntityId.Should().Be("script-actor-1");
        runtimeLease.RunId.Should().Be("run-1");
        runtimeLease.SessionId.Should().Be("run-1");
        runtimeLease.Context.RootActorId.Should().Be("script-actor-1");

        hub.SubscribeCalls.Should().Be(1);
        hub.LastRootActorId.Should().Be("script-actor-1");
        hub.LastSessionId.Should().Be("run-1");
        sink.Events.Should().ContainSingle().Which.RunFinished.RunId.Should().Be("run-1");
        hub.DisposedSubscriptions.Should().Be(1);
        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(attachment.ProjectionLease);
    }

    [Fact]
    public async Task DisabledProjection_ShouldNotActivateAttachOrRelease()
    {
        var release = new RecordingReleaseService();
        var hub = new RecordingSessionEventHub();
        var port = new ScriptServiceAguiProjectionPort(
            new ServiceProjectionOptions { Enabled = false },
            release,
            hub,
            CreateAttachExistingLookup(new RecordingActorRuntime()));

        var attachment = await port.AttachExistingRunProjectionAsync(
            "script-actor-1",
            "run-1",
            new RecordingEventSink(),
            CancellationToken.None);
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

        attachment.Should().BeNull();
        hub.SubscribeCalls.Should().Be(0);
        release.Leases.Should().BeEmpty();
    }

    [Fact]
    public void ScriptServiceAguiProjectionPort_ShouldNotExposePublicEnsureProjectionApi()
    {
        typeof(IScriptServiceAguiProjectionPort)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .NotContain(name => name.StartsWith("Ensure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachExistingRunProjection_ShouldAttachSink_WhenProjectionScopeActorExists()
    {
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.KnownActorIds.Add(BuildScopeActorId(
            "script-actor-1",
            ScriptServiceAguiProjectionKind,
            ProjectionRuntimeMode.SessionObservation,
            "run-1"));
        var port = new ScriptServiceAguiProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
        var sink = new RecordingEventSink();

        var attachment = await port.AttachExistingRunProjectionAsync(
            "script-actor-1",
            "run-1",
            sink,
            CancellationToken.None);

        attachment.Should().NotBeNull();
        var lease = attachment!.ProjectionLease.Should().BeOfType<ScriptServiceAguiRuntimeLease>().Subject;
        lease.ActorId.Should().Be("script-actor-1");
        lease.RunId.Should().Be("run-1");
        hub.SubscribeCalls.Should().Be(1);
        hub.LastRootActorId.Should().Be("script-actor-1");
        hub.LastSessionId.Should().Be("run-1");

        await hub.Handler!(new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "script-actor-1",
                RunId = "run-1",
            },
        });
        sink.Events.Should().ContainSingle().Which.RunFinished.RunId.Should().Be("run-1");
        attachment.LiveSinkLease.Should().NotBeNull();
        await attachment.LiveSinkLease!.DisposeAsync();
        hub.DisposedSubscriptions.Should().Be(1);
    }

    [Fact]
    public async Task AttachExistingRunProjection_ShouldReturnNull_WhenProjectionIsColdOrInvalid()
    {
        var hub = new RecordingSessionEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.KnownActorIds.Add("different-scope");
        var disabledPort = new ScriptServiceAguiProjectionPort(
            new ServiceProjectionOptions { Enabled = false },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
        var enabledPort = new ScriptServiceAguiProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));

        (await disabledPort.AttachExistingRunProjectionAsync(
            "script-actor-1",
            "run-1",
            new RecordingEventSink(),
            CancellationToken.None)).Should().BeNull();
        (await enabledPort.AttachExistingRunProjectionAsync(
            "script-actor-1",
            "run-1",
            new RecordingEventSink(),
            CancellationToken.None)).Should().BeNull();
        (await enabledPort.AttachExistingRunProjectionAsync(
            "",
            "run-1",
            new RecordingEventSink(),
            CancellationToken.None)).Should().BeNull();
        (await enabledPort.AttachExistingRunProjectionAsync(
            "script-actor-1",
            " ",
            new RecordingEventSink(),
            CancellationToken.None)).Should().BeNull();
        hub.SubscribeCalls.Should().Be(0);
    }

    [Fact]
    public void ScriptServiceAguiProjectionPort_ShouldValidateAttachExistingLookupDependency()
    {
        var create = () => new ScriptServiceAguiProjectionPort(
            new ServiceProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            new RecordingSessionEventHub(),
            null!);

        create.Should().Throw<ArgumentNullException>().WithParameterName("attachExistingLeaseLookup");
    }

    private static string BuildScopeActorId(
        string actorId,
        string projectionKind,
        ProjectionRuntimeMode mode,
        string sessionId) =>
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(actorId, projectionKind, mode, sessionId));

    private static IProjectionScopeAttachExistingLeaseLookup<ScriptServiceAguiRuntimeLease> CreateAttachExistingLookup(
        IActorRuntime runtime) =>
        new ProjectionScopeAttachExistingLeaseLookup<ScriptServiceAguiRuntimeLease, ScriptServiceAguiProjectionContext>(
            runtime,
            static request => new ScriptServiceAguiProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            },
            static (_, context) => new ScriptServiceAguiRuntimeLease(context));

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
        public string? LastRootActorId { get; private set; }
        public string? LastSessionId { get; private set; }
        public Func<AGUIEvent, ValueTask>? Handler { get; private set; }

        public Task PublishAsync(string rootActorId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<AGUIEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SubscribeCalls++;
            LastRootActorId = rootActorId;
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
}
