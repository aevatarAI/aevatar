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
    public async Task ResolveAsync_WhenTargetRoleProvided_ShouldReturnRoleActor()
    {
        var resolver = new WorkflowStepTargetAgentResolver();
        var ctx = new StubEventHandlerContext("workflow:root");
        var request = new StepRequestEvent
        {
            StepId = "notify",
            StepType = "llm_call",
            TargetRole = "telegram_user_bridge",
        };

        var result = await resolver.ResolveAsync(request, ctx, CancellationToken.None);

        result.UseSelf.Should().BeFalse();
        result.ActorId.Should().Be("workflow:root:telegram_user_bridge");
        result.Mode.Should().Be("target_role:telegram_user_bridge");
    }

    [Fact]
    public async Task ResolveAsync_WhenLlmCallOmitsTargetRole_ShouldUseImplicitAssistantRole()
    {
        var resolver = new WorkflowStepTargetAgentResolver();
        var ctx = new StubEventHandlerContext("workflow:root");
        var request = new StepRequestEvent
        {
            StepId = "answer",
            StepType = "llm_call",
        };

        var result = await resolver.ResolveAsync(request, ctx, CancellationToken.None);

        result.UseSelf.Should().BeFalse();
        result.ActorId.Should().Be("workflow:root:assistant");
        result.Mode.Should().Be("implicit_target_role:assistant");
    }

    [Fact]
    public async Task ResolveAsync_WhenNonLlmStepOmitsTargetRole_ShouldUseSelf()
    {
        var resolver = new WorkflowStepTargetAgentResolver();
        var ctx = new StubEventHandlerContext("workflow:root");
        var request = new StepRequestEvent
        {
            StepId = "normalize",
            StepType = "transform",
        };

        var result = await resolver.ResolveAsync(request, ctx, CancellationToken.None);

        result.UseSelf.Should().BeTrue();
        result.WorkerId.Should().Be("workflow:root");
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

    private sealed class TestTargetAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("test-target");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
