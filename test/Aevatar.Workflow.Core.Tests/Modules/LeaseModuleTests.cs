using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class LeaseModuleTests
{
    [Fact]
    public async Task HandleAsync_WhenAcquireRequested_ShouldCreateLeaseActorRecordPendingAndNotCompleteSameTurn()
    {
        var runtime = new RecordingActorRuntime();
        var ctx = new RecordingWorkflowContext(runtime);
        var module = new LeaseModule(runtime);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "acquire-1",
                StepType = "lease",
                RunId = "run-1",
                Input = "payload",
                Parameters =
                {
                    ["key"] = " Shared/Resource ",
                    ["on_conflict"] = "wait",
                    ["ttl_ms"] = "60000",
                    ["holder_token_variable"] = "lease_token",
                },
            }, id: "origin-1"),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
        runtime.Created.Should().ContainSingle().Which.Id.Should().Be(WorkflowLeaseActorId.FromKey("shared/resource"));
        ctx.Sent.Should().ContainSingle();
        var request = ctx.Sent[0].Event.Should().BeOfType<WorkflowLeaseAcquireRequestedEvent>().Subject;
        request.LeaseKey.Should().Be("shared/resource");
        request.OnConflict.Should().Be(WorkflowLeaseConflictPolicy.Wait);
        request.TtlMs.Should().Be(60_000);
        var state = ctx.LoadState<WorkflowLeaseModuleState>("lease");
        state.Pending.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WhenAcquireGrantMatchesPending_ShouldCompleteWithAnnotationsAndAssignedToken()
    {
        var runtime = new RecordingActorRuntime();
        var ctx = new RecordingWorkflowContext(runtime);
        var module = new LeaseModule(runtime);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "acquire-1",
                StepType = "lease",
                RunId = "run-1",
                Input = "payload",
                Parameters =
                {
                    ["key"] = "shared",
                    ["holder_token_variable"] = "lease_token",
                },
            }, id: "origin-1"),
            ctx,
            CancellationToken.None);
        var request = ctx.Sent.Select(x => x.Event).OfType<WorkflowLeaseAcquireRequestedEvent>().Single();

        await module.HandleAsync(
            Envelope(new WorkflowLeaseAcquiredEvent
            {
                LeaseKey = request.LeaseKey,
                RequestId = request.RequestId,
                RequesterRunId = request.RequesterRunId,
                RequesterActorId = request.RequesterActorId,
                RequesterStepId = request.RequesterStepId,
                HolderToken = "token-1",
                Generation = 4,
                ExpiresAtUnixMs = 123456,
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("token-1");
        completed.AssignedVariable.Should().Be("lease_token");
        completed.AssignedValue.Should().Be("token-1");
        completed.Annotations["lease.key"].Should().Be("shared");
        completed.Annotations["lease.actor_id"].Should().Be(WorkflowLeaseActorId.FromKey("shared"));
        completed.Annotations["lease.holder_token"].Should().Be("token-1");
        completed.Annotations["lease.generation"].Should().Be("4");
        completed.Annotations["lease.expires_at_unix_ms"].Should().Be("123456");
        ctx.LoadState<WorkflowLeaseModuleState>("lease").Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenReplyDoesNotMatchPending_ShouldIgnore()
    {
        var runtime = new RecordingActorRuntime();
        var ctx = new RecordingWorkflowContext(runtime);
        var module = new LeaseModule(runtime);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "acquire-1",
                StepType = "lease",
                RunId = "run-1",
                Parameters = { ["key"] = "shared" },
            }, id: "origin-1"),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new WorkflowLeaseAcquiredEvent
            {
                LeaseKey = "shared",
                RequestId = "wrong",
                RequesterRunId = "run-1",
                RequesterActorId = ctx.AgentId,
                RequesterStepId = "acquire-1",
                HolderToken = "token-1",
                Generation = 1,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
        ctx.LoadState<WorkflowLeaseModuleState>("lease").Pending.Should().ContainSingle();
    }

    [Theory]
    [InlineData("renew", "holder_token")]
    [InlineData("release", "generation")]
    public async Task HandleAsync_WhenRenewOrReleaseCredentialMissing_ShouldFailLocally(string action, string missing)
    {
        var runtime = new RecordingActorRuntime();
        var ctx = new RecordingWorkflowContext(runtime);
        var module = new LeaseModule(runtime);
        var request = new StepRequestEvent
        {
            StepId = $"{action}-1",
            StepType = "lease",
            RunId = "run-1",
            Parameters =
            {
                ["action"] = action,
                ["key"] = "shared",
                ["holder_token"] = "token-1",
                ["generation"] = "7",
            },
        };
        request.Parameters.Remove(missing);

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain(missing);
        ctx.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenGenerationMalformed_ShouldFailLocally()
    {
        var runtime = new RecordingActorRuntime();
        var ctx = new RecordingWorkflowContext(runtime);
        var module = new LeaseModule(runtime);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "renew-1",
                StepType = "lease",
                RunId = "run-1",
                Parameters =
                {
                    ["action"] = "renew",
                    ["key"] = "shared",
                    ["holder_token"] = "token-1",
                    ["generation"] = "not-number",
                },
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("generation");
        ctx.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenRenewAndReleaseRepliesMatch_ShouldComplete()
    {
        var runtime = new RecordingActorRuntime();
        var ctx = new RecordingWorkflowContext(runtime);
        var module = new LeaseModule(runtime);

        await module.HandleAsync(Envelope(new StepRequestEvent
        {
            StepId = "renew-1",
            StepType = "lease",
            RunId = "run-1",
            Parameters =
            {
                ["action"] = "renew",
                ["key"] = "shared",
                ["holder_token"] = "token-1",
                ["generation"] = "7",
            },
        }, id: "renew-origin"), ctx, CancellationToken.None);
        var renewRequest = ctx.Sent.Select(x => x.Event).OfType<WorkflowLeaseRenewRequestedEvent>().Single();

        await module.HandleAsync(Envelope(new WorkflowLeaseRenewedEvent
        {
            LeaseKey = "shared",
            RequestId = renewRequest.RequestId,
            RequesterRunId = "run-1",
            RequesterActorId = ctx.AgentId,
            RequesterStepId = "renew-1",
            HolderToken = "token-1",
            Generation = 7,
            ExpiresAtUnixMs = 555,
        }), ctx, CancellationToken.None);

        await module.HandleAsync(Envelope(new StepRequestEvent
        {
            StepId = "release-1",
            StepType = "lease",
            RunId = "run-1",
            Parameters =
            {
                ["action"] = "release",
                ["key"] = "shared",
                ["holder_token"] = "token-1",
                ["generation"] = "7",
            },
        }, id: "release-origin"), ctx, CancellationToken.None);
        var releaseRequest = ctx.Sent.Select(x => x.Event).OfType<WorkflowLeaseReleaseRequestedEvent>().Single();

        await module.HandleAsync(Envelope(new WorkflowLeaseReleasedEvent
        {
            LeaseKey = "shared",
            RequestId = releaseRequest.RequestId,
            RequesterRunId = "run-1",
            RequesterActorId = ctx.AgentId,
            RequesterStepId = "release-1",
            HolderToken = "token-1",
            Generation = 7,
        }), ctx, CancellationToken.None);

        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>()
            .Should().HaveCount(2)
            .And.OnlyContain(x => x.Success);
    }

    [Fact]
    public async Task HandleAsync_WhenRejectedMatchesPending_ShouldFailWithTypedReason()
    {
        var runtime = new RecordingActorRuntime();
        var ctx = new RecordingWorkflowContext(runtime);
        var module = new LeaseModule(runtime);
        await module.HandleAsync(Envelope(new StepRequestEvent
        {
            StepId = "acquire-1",
            StepType = "lease",
            RunId = "run-1",
            Parameters = { ["key"] = "shared" },
        }, id: "origin-1"), ctx, CancellationToken.None);
        var request = ctx.Sent.Select(x => x.Event).OfType<WorkflowLeaseAcquireRequestedEvent>().Single();

        await module.HandleAsync(Envelope(new WorkflowLeaseRejectedEvent
        {
            LeaseKey = "shared",
            RequestId = request.RequestId,
            RequesterRunId = "run-1",
            RequesterActorId = ctx.AgentId,
            RequesterStepId = "acquire-1",
            Operation = WorkflowLeaseOperation.Acquire,
            Reason = WorkflowLeaseRejectionReason.LeaseBusy,
            CurrentHolderRunId = "run-holder",
            Error = "busy",
        }), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("busy");
        completed.Annotations["lease.rejection_reason"].Should().Be(WorkflowLeaseRejectionReason.LeaseBusy.ToString());
        completed.Annotations["lease.current_holder_run_id"].Should().Be("run-holder");
    }

    private static EventEnvelope Envelope(IMessage evt, string? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private sealed class RecordingWorkflowContext(IActorRuntime runtime) : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "workflow-run-actor";

        public string RunId => "run-1";

        public IServiceProvider Services { get; } = new TestServiceProvider(runtime);

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Sent.Add((targetActorId, evt));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public List<(System.Type Type, string Id)> Created { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"{agentType.Name}:{Guid.NewGuid():N}";
            var actor = new StubActor(actorId);
            _actors[actorId] = actor;
            Created.Add((agentType, actorId));
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.GetValueOrDefault(id));

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;

        public IAgent Agent { get; } = new StubAgent(id);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestServiceProvider(IActorRuntime runtime) : IServiceProvider
    {
        public object? GetService(System.Type serviceType) =>
            serviceType == typeof(IActorRuntime)
                ? runtime
                : null;
    }
}
