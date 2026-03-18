using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Primitives
{
    public sealed class WorkflowStepTargetAgentResolverAdditionalTests
    {
        [Fact]
        public async Task ResolveAsync_WhenRuntimeMissing_ShouldThrow()
        {
            var resolver = new WorkflowStepTargetAgentResolver(
                [new FixedAliasProvider("telegram", typeof(TestTargetAgent))]);
            var request = new StepRequestEvent
            {
                StepId = "notify",
            };
            request.Parameters["agent_type"] = "telegram";

            var act = () => resolver.ResolveAsync(request, new StubEventHandlerContext("workflow:root"), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*no IActorRuntime is available*");
        }

        [Fact]
        public async Task ResolveAsync_WhenResolvedTypeIsNotAgent_ShouldThrow()
        {
            var resolver = new WorkflowStepTargetAgentResolver(
                new RecordingActorRuntime(),
                [new FixedAliasProvider("not-agent", typeof(NonAgentType))]);
            var request = new StepRequestEvent
            {
                StepId = "notify",
            };
            request.Parameters["agent_type"] = "not-agent";

            var act = () => resolver.ResolveAsync(request, new StubEventHandlerContext("workflow:root"), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*which is not an IAgent*");
        }

        [Fact]
        public async Task ResolveAsync_WhenExistingActorMatchesType_ShouldReuseWithoutCreating()
        {
            var runtime = new RecordingActorRuntime();
            runtime.Seed("bridge:telegram:prod", new TestTargetAgent("bridge:telegram:prod"));
            var resolver = new WorkflowStepTargetAgentResolver(
                runtime,
                [new FixedAliasProvider("telegram", typeof(TestTargetAgent))]);
            var request = new StepRequestEvent
            {
                StepId = "notify",
            };
            request.Parameters["agent_type"] = "telegram";
            request.Parameters["agent_id"] = "bridge:telegram:prod";

            var result = await resolver.ResolveAsync(request, new StubEventHandlerContext("workflow:root"), CancellationToken.None);

            result.ActorId.Should().Be("bridge:telegram:prod");
            runtime.CreateCalls.Should().Be(0);
            runtime.Links.Should().ContainSingle()
                .Which.Should().Be(("workflow:root", "bridge:telegram:prod"));
        }

        [Fact]
        public async Task ResolveAsync_WhenExistingActorHasDifferentType_ShouldThrow()
        {
            var runtime = new RecordingActorRuntime();
            runtime.Seed("bridge:telegram:prod", new OtherTargetAgent("bridge:telegram:prod"));
            var resolver = new WorkflowStepTargetAgentResolver(
                runtime,
                [new FixedAliasProvider("telegram", typeof(TestTargetAgent))]);
            var request = new StepRequestEvent
            {
                StepId = "notify",
            };
            request.Parameters["agent_type"] = "telegram";
            request.Parameters["agent_id"] = "bridge:telegram:prod";

            var act = () => resolver.ResolveAsync(request, new StubEventHandlerContext("workflow:root"), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*already exists with agent type*");
        }

        [Fact]
        public async Task ResolveAsync_WhenLinkFails_ShouldWrapFailure()
        {
            var runtime = new RecordingActorRuntime
            {
                LinkException = new InvalidOperationException("link failed"),
            };
            var resolver = new WorkflowStepTargetAgentResolver(
                runtime,
                [new FixedAliasProvider("telegram", typeof(TestTargetAgent))]);
            var request = new StepRequestEvent
            {
                StepId = "notify",
            };
            request.Parameters["agent_type"] = "telegram";
            request.Parameters["agent_id"] = "bridge:telegram:prod";

            var act = () => resolver.ResolveAsync(request, new StubEventHandlerContext("workflow:root"), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*failed to link it under workflow actor*");
        }

        [Fact]
        public async Task ResolveAsync_WhenParameterNamesUseDifferentCase_ShouldStillResolve()
        {
            var runtime = new RecordingActorRuntime();
            var resolver = new WorkflowStepTargetAgentResolver(
                runtime,
                [new FixedAliasProvider("telegram", typeof(TestTargetAgent))]);
            var request = new StepRequestEvent
            {
                StepId = "notify",
            };
            request.Parameters["AGENT_TYPE"] = "telegram";
            request.Parameters["AGENT_ID"] = " bridge:telegram:prod ";

            var result = await resolver.ResolveAsync(request, new StubEventHandlerContext("workflow:root"), CancellationToken.None);

            result.ActorId.Should().Be("bridge:telegram:prod");
            runtime.Created.Should().ContainSingle(x => x.actorId == "bridge:telegram:prod");
        }

        [Fact]
        public async Task ResolveAsync_WhenTypeNameIsUnknown_ShouldThrow()
        {
            var resolver = new WorkflowStepTargetAgentResolver(new RecordingActorRuntime());
            var request = new StepRequestEvent
            {
                StepId = "notify",
            };
            request.Parameters["agent_type"] = "NoSuchAgentType";

            var act = () => resolver.ResolveAsync(request, new StubEventHandlerContext("workflow:root"), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*did not resolve to a loadable type*");
        }

        [Fact]
        public async Task ResolveAsync_WhenTypeNameIsAmbiguous_ShouldThrow()
        {
            var resolver = new WorkflowStepTargetAgentResolver(new RecordingActorRuntime());
            var request = new StepRequestEvent
            {
                StepId = "notify",
            };
            request.Parameters["agent_type"] = "DuplicateAgent";

            var act = () => resolver.ResolveAsync(request, new StubEventHandlerContext("workflow:root"), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*is ambiguous*");
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
            public int CreateCalls { get; private set; }
            public Exception? LinkException { get; set; }

            public void Seed(string actorId, IAgent agent)
            {
                _actors[actorId] = new StubActor(actorId, agent);
            }

            public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
                where TAgent : IAgent =>
                CreateAsync(typeof(TAgent), id, ct);

            public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
            {
                var actorId = id ?? Guid.NewGuid().ToString("N");
                var actor = new StubActor(actorId, (IAgent)Activator.CreateInstance(agentType, actorId)!);
                _actors[actorId] = actor;
                Created.Add((agentType, actorId));
                CreateCalls++;
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
                if (LinkException != null)
                    throw LinkException;

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

        private sealed class OtherTargetAgent(string id) : IAgent
        {
            public string Id { get; } = id;
            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
            public Task<string> GetDescriptionAsync() => Task.FromResult("other-target");
            public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
            public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        }

        private sealed class NonAgentType;

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
}

namespace Aevatar.Workflow.Core.Tests.Primitives.AmbiguousOne
{
    using Aevatar.Foundation.Abstractions;

    internal sealed class DuplicateAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("dup-one");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

namespace Aevatar.Workflow.Core.Tests.Primitives.AmbiguousTwo
{
    using Aevatar.Foundation.Abstractions;

    internal sealed class DuplicateAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("dup-two");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
