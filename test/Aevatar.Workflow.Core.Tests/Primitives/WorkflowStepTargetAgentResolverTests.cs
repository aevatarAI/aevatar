using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowStepTargetAgentResolverTests
{
    [Fact]
    public async Task ResolveAsync_WhenAgentTypeProvided_ShouldCreateAndReturnTargetActor()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new WorkflowStepTargetAgentResolver(
            runtime,
            [new FixedAliasProvider("telegram", typeof(TestTargetAgent))]);
        var ctx = new StubEventHandlerContext("workflow:root");
        var request = new StepRequestEvent
        {
            StepId = "notify",
            TargetRole = "legacy-role",
        };
        request.Parameters["agent_type"] = "telegram";
        request.Parameters["agent_id"] = "bridge:telegram:prod";

        var result = await resolver.ResolveAsync(request, ctx, CancellationToken.None);

        result.UseSelf.Should().BeFalse();
        result.ActorId.Should().Be("bridge:telegram:prod");
        result.Mode.Should().Contain("agent_type");
        runtime.Created.Should().ContainSingle();
        runtime.Created[0].agentType.Should().Be(typeof(TestTargetAgent));
        runtime.Created[0].actorId.Should().Be("bridge:telegram:prod");
        runtime.Links.Should().ContainSingle()
            .Which.Should().Be(("workflow:root", "bridge:telegram:prod"));
    }

    [Fact]
    public async Task ResolveAsync_WhenAgentTypeMissing_ShouldFallbackToRoleThenSelf()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new WorkflowStepTargetAgentResolver(runtime);
        var ctx = new StubEventHandlerContext("workflow:root");

        var roleRequest = new StepRequestEvent
        {
            StepId = "step-1",
            TargetRole = "assistant",
        };
        var roleResult = await resolver.ResolveAsync(roleRequest, ctx, CancellationToken.None);
        roleResult.UseSelf.Should().BeFalse();
        roleResult.ActorId.Should().Be("workflow:root:assistant");

        var selfRequest = new StepRequestEvent
        {
            StepId = "step-2",
        };
        var selfResult = await resolver.ResolveAsync(selfRequest, ctx, CancellationToken.None);
        selfResult.UseSelf.Should().BeTrue();
        selfResult.WorkerId.Should().Be("workflow:root");
    }

    [Fact]
    public async Task ResolveAsync_WhenAgentTypeAndNoAgentId_ShouldGenerateStableActorId()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new WorkflowStepTargetAgentResolver(
            runtime,
            [new FixedAliasProvider("telegram", typeof(TestTargetAgent))]);
        var ctx = new StubEventHandlerContext("workflow:main");
        var request = new StepRequestEvent
        {
            StepId = "notify-step",
        };
        request.Parameters["agent_type"] = "telegram";

        var result = await resolver.ResolveAsync(request, ctx, CancellationToken.None);

        result.ActorId.Should().StartWith("workflow:main:step:notify-step:agent:");
        runtime.Created.Should().ContainSingle();
        runtime.Created[0].actorId.Should().Be(result.ActorId);
        runtime.Links.Should().ContainSingle()
            .Which.Should().Be(("workflow:main", result.ActorId));
    }

    private sealed class FixedAliasProvider(string alias, Type type) : IWorkflowAgentTypeAliasProvider
    {
        public bool TryResolve(string inputAlias, out Type agentType)
        {
            if (string.Equals(alias, inputAlias, StringComparison.OrdinalIgnoreCase))
            {
                agentType = type;
                return true;
            }

            agentType = type;
            return false;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        public List<(Type agentType, string actorId)> Created { get; } = [];
        public List<(string parentId, string childId)> Links { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new StubActor(actorId, (IAgent)Activator.CreateInstance(agentType, actorId)!);
            _actors[actorId] = actor;
            Created.Add((agentType, actorId));
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _actors.TryGetValue(id, out var actor);
            return Task.FromResult(actor);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            Links.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestTargetAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("test-target");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubEventHandlerContext(string agentId) : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();
        public string AgentId => Agent.Id;
        public IAgent Agent { get; } = new TestTargetAgent(agentId);
        public IServiceProvider Services { get; } = new EmptyServiceProvider();
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
