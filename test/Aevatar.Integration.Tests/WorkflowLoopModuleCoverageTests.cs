using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowLoopModule")]
public sealed class WorkflowLoopModuleCoverageTests
{
    [Fact]
    public void CanHandle_ShouldMatchStartAndStepCompleted()
    {
        var module = new WorkflowLoopModule();

        module.CanHandle(Envelope(new StartWorkflowEvent())).Should().BeTrue();
        module.CanHandle(Envelope(new StepCompletedEvent())).Should().BeTrue();
        module.CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenWorkflowNotSet_ShouldNoop()
    {
        var module = new WorkflowLoopModule();
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { Input = "x" }), ctx, CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenWorkflowHasNoSteps_ShouldPublishFailure()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(new WorkflowDefinition
        {
            Name = "wf-empty",
            Roles = [],
            Steps = [],
        });
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { Input = "x" }), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("无步骤");
    }

    [Fact]
    public async Task HandleAsync_ShouldDispatchStepAdvanceAndComplete()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "connector_call",
                TargetRole = "coordinator",
                Parameters = new Dictionary<string, string> { ["connector"] = "conn-a" },
            },
            new StepDefinition
            {
                Id = "s2",
                Type = "transform",
            }));
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { Input = "hello" }), ctx, CancellationToken.None);
        var firstRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        firstRequest.StepId.Should().Be("s1");
        firstRequest.Input.Should().Be("hello");
        firstRequest.Parameters["allowed_connectors"].Should().Be("conn-a,conn-b");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", Success = true, Output = "next-input" }),
            ctx,
            CancellationToken.None);
        var secondRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        secondRequest.StepId.Should().Be("s2");
        secondRequest.Input.Should().Be("next-input");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s2", Success = true, Output = "done" }),
            ctx,
            CancellationToken.None);
        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("done");
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyRunning_ShouldPublishFailure()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" }));
        var ctx = CreateContext();
        const string runId = "run-already-running";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "first" }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "second" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<WorkflowCompletedEvent>().Single();
        completed.Success.Should().BeFalse();
        completed.RunId.Should().Be(runId);
        completed.Error.Should().Contain("already active");
    }

    [Fact]
    public async Task HandleAsync_WhenStepFails_ShouldPublishWorkflowFailure()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" }));
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { Input = "start" }), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", Success = false, Error = "boom" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("boom");
    }

    [Fact]
    public async Task HandleAsync_WhenCompletionStepIsUnknown_ShouldIgnore()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" }));
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { Input = "start" }), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1_internal_sub_1", Success = true, Output = "x" }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    private static WorkflowDefinition BuildWorkflow(params StepDefinition[] steps)
    {
        return new WorkflowDefinition
        {
            Name = "wf",
            Roles =
            [
                new RoleDefinition
                {
                    Id = "coordinator",
                    Name = "Coordinator",
                    Connectors = ["conn-a", "conn-b"],
                },
            ],
            Steps = steps.ToList(),
        };
    }

    private static RecordingEventHandlerContext CreateContext()
    {
        return new RecordingEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            new StubAgent("workflow-loop-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "test-publisher",
            Direction = EventDirection.Self,
        };
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public RecordingEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            Agent = agent;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => Agent.Id;
        public IAgent Agent { get; }
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }
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
}
