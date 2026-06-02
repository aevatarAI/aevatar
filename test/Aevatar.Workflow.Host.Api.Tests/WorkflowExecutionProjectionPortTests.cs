using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionProjectionPortTests
{
    [Fact]
    public void WorkflowExecutionProjectionPort_ShouldNotExposePublicEnsureProjectionApi()
    {
        typeof(IWorkflowExecutionProjectionPort)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .NotContain(name => name.StartsWith("Ensure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachAndDetachLiveSinkAsync_ShouldBridgeSessionHubSubscription()
    {
        var hub = new RecordingRunEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("projection.session.scope:workflow-execution-session:actor-1:cmd-1");
        var port = new WorkflowExecutionProjectionPort(
            new WorkflowExecutionProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
        var lease = new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-session",
            SessionId = "cmd-1",
        });
        var sink = new RecordingRunEventSink();

        var liveSinkLease = await port.AttachLiveSinkAsync(lease, sink);
        await hub.Handler!(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload { Name = "event-1" },
        });
        await port.DetachLiveSinkAsync(liveSinkLease);

        hub.SubscribeCalls.Should().Be(1);
        hub.LastRootActorId.Should().Be("actor-1");
        hub.LastSessionId.Should().Be("cmd-1");
        sink.Events.Should().ContainSingle();
        sink.Events[0].Custom.Name.Should().Be("event-1");
        hub.LastSubscription!.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task AttachExistingActorProjectionAsync_ShouldAttachOnlyWhenProjectionSessionExists()
    {
        var hub = new RecordingRunEventHub();
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("projection.session.scope:workflow-execution-session:actor-1:cmd-1");
        var port = new WorkflowExecutionProjectionPort(
            new WorkflowExecutionProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));
        var sink = new RecordingRunEventSink();

        var attachment = await port.AttachExistingActorProjectionAsync("actor-1", "cmd-1", sink);

        attachment.Should().NotBeNull();
        attachment!.ProjectionLease.ActorId.Should().Be("actor-1");
        attachment.ProjectionLease.CommandId.Should().Be("cmd-1");
        hub.SubscribeCalls.Should().Be(1);
        hub.LastRootActorId.Should().Be("actor-1");
        hub.LastSessionId.Should().Be("cmd-1");
        runtime.ExistsCalls.Should().ContainSingle()
            .Which.Should().Be("projection.session.scope:workflow-execution-session:actor-1:cmd-1");
    }

    [Fact]
    public async Task AttachExistingActorProjectionAsync_ShouldReturnNull_WhenProjectionSessionIsCold()
    {
        var hub = new RecordingRunEventHub();
        var runtime = new RecordingActorRuntime();
        var port = new WorkflowExecutionProjectionPort(
            new WorkflowExecutionProjectionOptions { Enabled = true },
            new RecordingReleaseService(),
            hub,
            CreateAttachExistingLookup(runtime));

        var attachment = await port.AttachExistingActorProjectionAsync(
            "actor-1",
            "cmd-1",
            new RecordingRunEventSink());

        attachment.Should().BeNull();
        hub.SubscribeCalls.Should().Be(0);
        runtime.ExistsCalls.Should().ContainSingle()
            .Which.Should().Be("projection.session.scope:workflow-execution-session:actor-1:cmd-1");
    }

    [Fact]
    public async Task ReleaseActorProjectionAsync_ShouldDelegateToReleaseService()
    {
        var release = new RecordingReleaseService();
        var port = new WorkflowExecutionProjectionPort(
            new WorkflowExecutionProjectionOptions { Enabled = true },
            release,
            new RecordingRunEventHub(),
            CreateAttachExistingLookup(new RecordingActorRuntime()));
        var lease = new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-session",
            SessionId = "cmd-1",
        });

        await port.ReleaseActorProjectionAsync(lease);

        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(lease);
    }

    [Fact]
    public async Task WorkflowChatRunObservationScopeActivationPort_ShouldActivateWorkflowExecutionSession()
    {
        var activation = new RecordingActivationService();
        var port = new WorkflowChatRunObservationScopeActivationPort(
            activation,
            new RecordingReleaseService());

        var result = await port.ActivateAsync("actor-1", "cmd-1", CancellationToken.None);

        result.Should().Be(new WorkflowChatRunObservationScopeActivation("actor-1", "cmd-1"));
        activation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProjectionScopeStartRequest
            {
                RootActorId = "actor-1",
                ProjectionKind = "workflow-execution-session",
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = "cmd-1",
            });
    }

    [Fact]
    public async Task WorkflowChatRunObservationScopeActivationPort_ShouldReturnNull_WhenActivationFails()
    {
        var port = new WorkflowChatRunObservationScopeActivationPort(
            new RecordingActivationService { Exception = new InvalidOperationException("activation failed") },
            new RecordingReleaseService());

        var result = await port.ActivateAsync("actor-1", "cmd-1", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowChatRunObservationScopeActivationPort_ShouldReleaseWorkflowExecutionSession()
    {
        var release = new RecordingReleaseService();
        var port = new WorkflowChatRunObservationScopeActivationPort(
            new RecordingActivationService(),
            release);

        await port.ReleaseAsync(
            new WorkflowChatRunObservationScopeActivation("actor-1", "cmd-1"),
            CancellationToken.None);

        release.Leases.Should().ContainSingle();
        var lease = release.Leases.Single();
        lease.ActorId.Should().Be("actor-1");
        lease.CommandId.Should().Be("cmd-1");
        lease.Context.ProjectionKind.Should().Be("workflow-execution-session");
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _existingActors = new(StringComparer.Ordinal);

        public List<string> ExistsCalls { get; } = [];

        public void MarkExists(string actorId) => _existingActors.Add(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id)
        {
            ExistsCalls.Add(id);
            return Task.FromResult(_existingActors.Contains(id));
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingActivationService : IProjectionScopeActivationService<WorkflowExecutionRuntimeLease>
    {
        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public WorkflowExecutionRuntimeLease LeaseToReturn { get; } = new(new WorkflowExecutionProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-session",
            SessionId = "cmd-1",
        });

        public Exception? Exception { get; set; }

        public Task<WorkflowExecutionRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Exception != null)
                throw Exception;

            return Task.FromResult(LeaseToReturn);
        }
    }

    private sealed class RecordingReleaseService : IProjectionScopeReleaseService<WorkflowExecutionRuntimeLease>
    {
        public List<WorkflowExecutionRuntimeLease> Leases { get; } = [];

        public Task ReleaseIfIdleAsync(WorkflowExecutionRuntimeLease lease, CancellationToken ct = default)
        {
            Leases.Add(lease);
            return Task.CompletedTask;
        }
    }

    private static IProjectionScopeAttachExistingLeaseLookup<WorkflowExecutionRuntimeLease> CreateAttachExistingLookup(
        IActorRuntime runtime) =>
        new ProjectionScopeAttachExistingLeaseLookup<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext>(
            runtime,
            request => new WorkflowExecutionProjectionContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
                SessionId = request.SessionId,
            },
            (_, context) => new WorkflowExecutionRuntimeLease(context));

    private sealed class RecordingRunEventHub : IProjectionSessionEventHub<WorkflowRunEventEnvelope>
    {
        public int SubscribeCalls { get; private set; }

        public string? LastRootActorId { get; private set; }

        public string? LastSessionId { get; private set; }

        public Func<WorkflowRunEventEnvelope, ValueTask>? Handler { get; private set; }

        public RecordingSubscription? LastSubscription { get; private set; }

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            WorkflowRunEventEnvelope evt,
            CancellationToken ct = default)
        {
            _ = rootActorId;
            _ = sessionId;
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<WorkflowRunEventEnvelope, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SubscribeCalls++;
            LastRootActorId = rootActorId;
            LastSessionId = sessionId;
            Handler = handler;
            LastSubscription = new RecordingSubscription();
            return Task.FromResult<IAsyncDisposable>(LastSubscription);
        }
    }

    private sealed class RecordingSubscription : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRunEventSink : IEventSink<WorkflowRunEventEnvelope>
    {
        public List<WorkflowRunEventEnvelope> Events { get; } = [];

        public void Push(WorkflowRunEventEnvelope evt) => Events.Add(evt);

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
