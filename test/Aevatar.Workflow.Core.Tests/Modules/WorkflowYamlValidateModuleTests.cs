using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowYamlValidateModuleTests
{
    [Fact]
    public void CanHandle_ShouldOnlyAcceptStepRequestEnvelopes()
    {
        var module = new WorkflowYamlValidateModule();

        module.CanHandle(new EventEnvelope { Payload = Any.Pack(new StepRequestEvent()) }).Should().BeTrue();
        module.CanHandle(new EventEnvelope { Payload = Any.Pack(new WorkflowCompletedEvent()) }).Should().BeFalse();
        module.CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldIgnoreUnsupportedEnvelopeOrStepType()
    {
        var module = new WorkflowYamlValidateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            new EventEnvelope { Payload = Any.Pack(new WorkflowCompletedEvent()) },
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            new EventEnvelope
            {
                Payload = Any.Pack(new StepRequestEvent
                {
                    StepId = "step-1",
                    RunId = "run-1",
                    StepType = "transform",
                }),
            },
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldFailWhenYamlIsMissing()
    {
        var module = new WorkflowYamlValidateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            new EventEnvelope
            {
                Payload = Any.Pack(new StepRequestEvent
                {
                    StepId = "step-1",
                    RunId = "run-1",
                    StepType = "workflow_yaml_validate",
                    Input = "plain text only",
                }),
            },
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("No workflow YAML found");
    }

    [Fact]
    public async Task HandleAsync_ShouldFailWhenYamlIsInvalid()
    {
        var module = new WorkflowYamlValidateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            new EventEnvelope
            {
                Payload = Any.Pack(new StepRequestEvent
                {
                    StepId = "step-2",
                    RunId = "run-2",
                    StepType = "workflow_yaml_validate",
                    Input =
                        """
                        ```yaml
                        name: bad
                        roles: []
                        steps:
                          - id: x
                            type: unknown_step
                        ```
                        """,
                }),
            },
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("Invalid workflow YAML");
    }

    [Fact]
    public async Task HandleAsync_ShouldPublishCanonicalYamlWhenValid()
    {
        var module = new WorkflowYamlValidateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            new EventEnvelope
            {
                Payload = Any.Pack(new StepRequestEvent
                {
                    StepId = "step-3",
                    RunId = "run-3",
                    StepType = "workflow_yaml_validate",
                    Input =
                        """
                        prefix
                        ```yaml
                        name: good
                        roles: []
                        steps:
                          - id: s1
                            type: transform
                        ```
                        suffix
                        """,
                }),
            },
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().StartWith("```yaml\nname: good");
        completion.Output.Should().EndWith("\n```");
    }

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public IServiceProvider Services { get; } = new TestServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, BroadcastDirection Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() => new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() => [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> => Task.CompletedTask;

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            BroadcastDirection direction = BroadcastDirection.Down,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct = default,
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
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 2, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyList<IWorkflowModulePack> _modulePacks = [new WorkflowCoreModulePack()];

        public object? GetService(global::System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IWorkflowModulePack>))
                return _modulePacks;

            return null;
        }
    }
}
