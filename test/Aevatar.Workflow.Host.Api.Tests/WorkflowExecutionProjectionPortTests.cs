using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
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
    public async Task EnsureActorProjectionAsync_ShouldStartWorkflowExecutionSession()
    {
        var activation = new RecordingActivationService();
        var port = new WorkflowExecutionProjectionPort(
            new WorkflowExecutionProjectionOptions { Enabled = true },
            activation,
            new RecordingReleaseService(),
            new RecordingRunEventHub());

        var lease = await port.EnsureActorProjectionAsync("actor-1", "cmd-1");

        lease.Should().BeSameAs(activation.LeaseToReturn);
        activation.Requests.Should().ContainSingle();
        activation.Requests[0].RootActorId.Should().Be("actor-1");
        activation.Requests[0].ProjectionKind.Should().Be("workflow-execution-session");
        activation.Requests[0].SessionId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task AttachAndDetachLiveSinkAsync_ShouldBridgeSessionHubSubscription()
    {
        var hub = new RecordingRunEventHub();
        var port = new WorkflowExecutionProjectionPort(
            new WorkflowExecutionProjectionOptions { Enabled = true },
            new RecordingActivationService(),
            new RecordingReleaseService(),
            hub);
        var lease = new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-session",
            SessionId = "cmd-1",
        });
        var sink = new RecordingRunEventSink();

        await port.AttachLiveSinkAsync(lease, sink);
        await hub.Handler!(new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload { Name = "event-1" },
        });
        await port.DetachLiveSinkAsync(lease, sink);

        hub.SubscribeCalls.Should().Be(1);
        hub.LastScopeId.Should().Be("actor-1");
        hub.LastSessionId.Should().Be("cmd-1");
        sink.Events.Should().ContainSingle();
        sink.Events[0].Custom.Name.Should().Be("event-1");
        hub.LastSubscription!.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task ReleaseActorProjectionAsync_ShouldDelegateToReleaseService()
    {
        var release = new RecordingReleaseService();
        var port = new WorkflowExecutionProjectionPort(
            new WorkflowExecutionProjectionOptions { Enabled = true },
            new RecordingActivationService(),
            release,
            new RecordingRunEventHub());
        var lease = new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "workflow-execution-session",
            SessionId = "cmd-1",
        });

        await port.ReleaseActorProjectionAsync(lease);

        release.Leases.Should().ContainSingle().Which.Should().BeSameAs(lease);
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

        public Task<WorkflowExecutionRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
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

    private sealed class RecordingRunEventHub : IProjectionSessionEventHub<WorkflowRunEventEnvelope>
    {
        public int SubscribeCalls { get; private set; }

        public string? LastScopeId { get; private set; }

        public string? LastSessionId { get; private set; }

        public Func<WorkflowRunEventEnvelope, ValueTask>? Handler { get; private set; }

        public RecordingSubscription? LastSubscription { get; private set; }

        public Task PublishAsync(
            string scopeId,
            string sessionId,
            WorkflowRunEventEnvelope evt,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowRunEventEnvelope, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SubscribeCalls++;
            LastScopeId = scopeId;
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
