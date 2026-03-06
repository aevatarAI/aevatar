using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;
using SystemType = System.Type;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowCoreModules")]
public sealed class WorkflowCoreModulesCoverageTests
{
    [Fact]
    public async Task WorkflowCallModule_ShouldPublishFailureWhenMissingWorkflow_AndEmitInvocationRequestWhenPresent()
    {
        var module = new WorkflowCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wf-1",
                StepType = "workflow_call",
                RunId = "default",
                Input = "payload",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("missing workflow parameter");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wf-2",
                StepType = "workflow_call",
                RunId = "default",
                Input = "payload-2",
                Parameters =
                {
                    ["workflow"] = "sub_flow",
                    ["lifecycle"] = "singleton",
                },
            }),
            ctx,
            CancellationToken.None);

        var invocation = ctx.Published.Select(x => x.evt).OfType<SubWorkflowInvokeRequestedEvent>().Single();
        invocation.WorkflowName.Should().Be("sub_flow");
        invocation.Input.Should().Be("payload-2");
        invocation.ParentStepId.Should().Be("wf-2");
        invocation.ParentRunId.Should().Be("default");
        invocation.Lifecycle.Should().Be("singleton");
        invocation.RequestedByActorId.Should().Be("workflow-test-agent");
        Regex.IsMatch(invocation.InvocationId, "^default:workflow_call:wf-2:[0-9a-f]{32}$")
            .Should().BeTrue("workflow_call invocation id should follow canonical format");
    }

    [Fact]
    public async Task WorkflowCallModule_ShouldRejectMissingStepIdAndUnsupportedLifecycle()
    {
        var module = new WorkflowCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = string.Empty,
                StepType = "workflow_call",
                RunId = "default",
                Parameters =
                {
                    ["workflow"] = "sub_flow",
                },
            }),
            ctx,
            CancellationToken.None);

        var missingStep = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        missingStep.Success.Should().BeFalse();
        missingStep.Error.Should().Contain("missing step_id");

        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wf-invalid",
                StepType = "workflow_call",
                RunId = "default",
                Parameters =
                {
                    ["workflow"] = "sub_flow",
                    ["lifecycle"] = "isolate",
                },
            }),
            ctx,
            CancellationToken.None);

        var invalidLifecycle = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        invalidLifecycle.Success.Should().BeFalse();
        invalidLifecycle.Error.Should().Contain("lifecycle must be singleton/transient/scope");
    }

    [Fact]
    public async Task WorkflowCallModule_ShouldIgnoreNonWorkflowCallSteps()
    {
        var module = new WorkflowCallModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "other",
                StepType = "transform",
                RunId = "default",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
        module.CanHandle(Envelope(new StepRequestEvent())).Should().BeTrue();
        module.CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    private static TestEventHandlerContext CreateContext() =>
        new(new ServiceCollection().BuildServiceProvider(), NullLogger.Instance);

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "workflow-test",
            Direction = EventDirection.Self,
        };

    private sealed class TestEventHandlerContext(IServiceProvider services, ILogger logger) : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "workflow-test-agent";

        public IAgent Agent { get; } = new StubAgent();

        public IServiceProvider Services { get; } = services;

        public ILogger Logger { get; } = logger;

        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down, CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "workflow-test-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<SystemType>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
