using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowRunGAgentSingletonReuseContractTests : WorkflowGAgentTestBase
{
    private const string ParentActorId = "workflow-run-parent-authority";
    private const string ChildActorId = "workflow-run-parent-authority:workflow:sub-flow";

    [Fact]
    public async Task WorkflowRunGAgent_SingleRunPolicy_ShouldRejectForgedSingletonUpgrade()
    {
        var eventStore = new InMemoryEventStore();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, ChildActorId);
        await BindInteractiveWorkflowRunDefinitionAsync(
            agent,
            "definition-top-level",
            BuildValidWorkflowYaml("role_a", "RoleA", workflowName: "top_level"),
            "top_level",
            runId: "top-level-run");
        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "top_level",
            RunId = "top-level-run",
            Success = true,
            Output = "top-level-done",
        });
        var terminalState = agent.State.Clone();
        var persistedCount = (await eventStore.GetEventsAsync(agent.Id)).Count;

        var act = () => DispatchAsync(
            agent,
            SingletonBind("forged-child-run", bindingGeneration: 1),
            ParentActorId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        agent.State.Equals(terminalState).Should().BeTrue();
        agent.State.ReusePolicy.Should().Be(WorkflowRunActorReusePolicy.SingleRun);
        agent.State.BindingGeneration.Should().Be(0);
        agent.State.ReuseAuthorityActorId.Should().BeEmpty();
        (await eventStore.GetEventsAsync(agent.Id)).Should().HaveCount(persistedCount);
    }

    [Fact]
    public async Task WorkflowRunGAgent_WorkflowCallSingleton_ShouldAdvanceOnlyAfterTerminalAndStartNextGeneration()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, ChildActorId);
        agent.EventPublisher = publisher;

        await DispatchAsync(agent, SingletonBind("child-run-1", bindingGeneration: 1), ParentActorId);
        await DispatchAsync(agent, SingletonStart("child-run-1", bindingGeneration: 1), ParentActorId);

        agent.State.RunId.Should().Be("child-run-1");
        agent.State.ReusePolicy.Should().Be(WorkflowRunActorReusePolicy.SerialSingleton);
        agent.State.BindingGeneration.Should().Be(1);
        agent.State.ReuseAuthorityActorId.Should().Be(ParentActorId);
        KernelState(agent).Active.Should().BeTrue();
        KernelState(agent).RunId.Should().Be("child-run-1");

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "sub_flow",
            RunId = "child-run-1",
            Success = true,
            Output = "first-done",
        });

        await DispatchAsync(agent, SingletonBind("child-run-2", bindingGeneration: 2), ParentActorId);
        await DispatchAsync(agent, SingletonStart("child-run-2", bindingGeneration: 2), ParentActorId);

        agent.State.RunId.Should().Be("child-run-2");
        agent.State.ReusePolicy.Should().Be(WorkflowRunActorReusePolicy.SerialSingleton);
        agent.State.BindingGeneration.Should().Be(2);
        agent.State.ReuseAuthorityActorId.Should().Be(ParentActorId);
        agent.State.FinalOutput.Should().BeEmpty();
        agent.State.FinalError.Should().BeEmpty();
        KernelState(agent).Active.Should().BeTrue();
        KernelState(agent).RunId.Should().Be("child-run-2");
        publisher.Published.Select(x => x.evt).OfType<StepRequestEvent>()
            .Select(x => x.RunId)
            .Should().Equal("child-run-1", "child-run-2");

        var persisted = await eventStore.GetEventsAsync(agent.Id);
        persisted.Where(x => x.EventData.Is(BindWorkflowRunDefinitionEvent.Descriptor))
            .Select(x => x.EventData.Unpack<BindWorkflowRunDefinitionEvent>().BindingGeneration)
            .Should().Equal(1, 2);
    }

    [Fact]
    public async Task WorkflowRunGAgent_WorkflowCallSingleton_ShouldFenceLatePriorGenerationTraffic()
    {
        var eventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, ChildActorId);
        agent.EventPublisher = publisher;

        await DispatchAsync(agent, SingletonBind("child-run-1", bindingGeneration: 1), ParentActorId);
        await DispatchAsync(agent, SingletonStart("child-run-1", bindingGeneration: 1), ParentActorId);
        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "sub_flow",
            RunId = "child-run-1",
            Success = true,
            Output = "first-done",
        });
        await DispatchAsync(agent, SingletonBind("child-run-2", bindingGeneration: 2), ParentActorId);
        await DispatchAsync(agent, SingletonStart("child-run-2", bindingGeneration: 2), ParentActorId);

        var activeSecondGeneration = agent.State.Clone();
        var persistedCount = (await eventStore.GetEventsAsync(agent.Id)).Count;
        var publishedCount = publisher.Published.Count;

        await DispatchAsync(agent, SingletonBind("child-run-1", bindingGeneration: 1), ParentActorId);
        await DispatchAsync(agent, SingletonStart("child-run-1", bindingGeneration: 1), ParentActorId);
        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "sub_flow",
            RunId = "child-run-1",
            Success = false,
            Error = "late-completion",
        });
        await agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            WorkflowName = "sub_flow",
            RunId = "child-run-1",
            Reason = "late-stop",
        });
        await agent.HandleWorkflowRunStoppedAsync(new WorkflowRunStoppedEvent
        {
            RunId = "child-run-1",
            Reason = "late-run-stop",
        });
        await DispatchAsync(agent, new StepCompletedEvent
        {
            StepId = "step_1",
            RunId = "child-run-1",
            Success = true,
            Output = "late-step",
        }, ParentActorId);

        agent.State.Equals(activeSecondGeneration).Should().BeTrue();
        agent.State.RunId.Should().Be("child-run-2");
        agent.State.BindingGeneration.Should().Be(2);
        KernelState(agent).Active.Should().BeTrue();
        KernelState(agent).RunId.Should().Be("child-run-2");
        publisher.Published.Should().HaveCount(publishedCount);
        (await eventStore.GetEventsAsync(agent.Id)).Should().HaveCount(persistedCount);
    }

    [Fact]
    public async Task WorkflowRunGAgent_WorkflowCallSingleton_ShouldRejectNextRunWhileCurrentGenerationIsActive()
    {
        var eventStore = new InMemoryEventStore();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, ChildActorId);

        await DispatchAsync(agent, SingletonBind("child-run-1", bindingGeneration: 1), ParentActorId);
        await DispatchAsync(agent, SingletonStart("child-run-1", bindingGeneration: 1), ParentActorId);
        var activeState = agent.State.Clone();
        var persistedCount = (await eventStore.GetEventsAsync(agent.Id)).Count;

        var act = () => DispatchAsync(
            agent,
            SingletonBind("child-run-2", bindingGeneration: 2),
            ParentActorId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        agent.State.Equals(activeState).Should().BeTrue();
        (await eventStore.GetEventsAsync(agent.Id)).Should().HaveCount(persistedCount);
    }

    [Fact]
    public async Task WorkflowRunGAgent_WorkflowCallSingleton_ShouldIgnoreExactSameGenerationBindRetry()
    {
        var eventStore = new InMemoryEventStore();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, ChildActorId);
        var binding = SingletonBind("child-run-1", bindingGeneration: 1);

        await DispatchAsync(agent, binding, ParentActorId);
        var boundState = agent.State.Clone();
        var persistedCount = (await eventStore.GetEventsAsync(agent.Id)).Count;

        await DispatchAsync(agent, binding.Clone(), ParentActorId);

        agent.State.Equals(boundState).Should().BeTrue();
        (await eventStore.GetEventsAsync(agent.Id)).Should().HaveCount(persistedCount);
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("scope")]
    [InlineData("version")]
    [InlineData("inline")]
    [InlineData("lineage")]
    public async Task WorkflowRunGAgent_WorkflowCallSingleton_ShouldRejectChangedSameGenerationBind(
        string mutation)
    {
        var eventStore = new InMemoryEventStore();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, ChildActorId);
        var binding = SingletonBind("child-run-1", bindingGeneration: 1);
        await DispatchAsync(agent, binding, ParentActorId);
        var boundState = agent.State.Clone();
        var persistedCount = (await eventStore.GetEventsAsync(agent.Id)).Count;
        var changed = binding.Clone();
        switch (mutation)
        {
            case "yaml":
                changed.WorkflowYaml = BuildValidWorkflowYaml("other_role", "OtherRole", workflowName: "sub_flow");
                break;
            case "scope":
                changed.ScopeId = "different-scope";
                break;
            case "version":
                changed.DefinitionVersion++;
                break;
            case "inline":
                changed.InlineWorkflowYamls["nested"] = BuildValidWorkflowYaml(
                    "nested_role",
                    "NestedRole",
                    workflowName: "nested");
                break;
            case "lineage":
                changed.InitialLineage.SubWorkflow.ParentStepId = "different-step";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        var act = () => DispatchAsync(agent, changed, ParentActorId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot change within a binding generation*");
        agent.State.Equals(boundState).Should().BeTrue();
        (await eventStore.GetEventsAsync(agent.Id)).Should().HaveCount(persistedCount);
    }

    [Theory]
    [InlineData(3, ParentActorId, ParentActorId)]
    [InlineData(2, "workflow-run-other-authority", "workflow-run-other-authority")]
    [InlineData(2, ParentActorId, "workflow-run-other-publisher")]
    public async Task WorkflowRunGAgent_WorkflowCallSingleton_ShouldRejectGenerationGapOrAuthorityMismatch(
        long nextGeneration,
        string requestedAuthority,
        string publisherActorId)
    {
        var eventStore = new InMemoryEventStore();
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, ChildActorId);

        await DispatchAsync(agent, SingletonBind("child-run-1", bindingGeneration: 1), ParentActorId);
        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "sub_flow",
            RunId = "child-run-1",
            Success = true,
            Output = "first-done",
        });
        var terminalState = agent.State.Clone();
        var persistedCount = (await eventStore.GetEventsAsync(agent.Id)).Count;

        var act = () => DispatchAsync(
            agent,
            SingletonBind(
                "child-run-2",
                bindingGeneration: nextGeneration,
                reuseAuthorityActorId: requestedAuthority),
            publisherActorId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        agent.State.Equals(terminalState).Should().BeTrue();
        (await eventStore.GetEventsAsync(agent.Id)).Should().HaveCount(persistedCount);
    }

    [Fact]
    public async Task WorkflowRunGAgent_ReplayContract_ShouldKeepNewestSingletonGenerationAndFencePriorRunEvents()
    {
        var eventStore = new InMemoryEventStore();
        var firstBind = SingletonBind("child-run-1", bindingGeneration: 1);
        var secondBind = SingletonBind("child-run-2", bindingGeneration: 2);
        var events = new IMessage[]
        {
            firstBind,
            new WorkflowRunExecutionStartedEvent
            {
                RunId = "child-run-1",
                WorkflowName = "sub_flow",
                Input = "first-input",
                StartedAtUtc = Timestamp.FromDateTime(DateTime.SpecifyKind(
                    new DateTime(2026, 8, 14, 1, 2, 3),
                    DateTimeKind.Utc)),
            },
            new WorkflowCompletedEvent
            {
                RunId = "child-run-1",
                WorkflowName = "sub_flow",
                Success = true,
                Output = "first-done",
            },
            secondBind,
            new WorkflowRunExecutionStartedEvent
            {
                RunId = "child-run-2",
                WorkflowName = "sub_flow",
                Input = "second-input",
                StartedAtUtc = Timestamp.FromDateTime(DateTime.SpecifyKind(
                    new DateTime(2026, 8, 14, 2, 3, 4),
                    DateTimeKind.Utc)),
            },
            firstBind,
            new WorkflowCompletedEvent
            {
                RunId = "child-run-1",
                WorkflowName = "sub_flow",
                Success = false,
                Error = "late-first-completion",
            },
            new WorkflowStoppedEvent
            {
                RunId = "child-run-1",
                WorkflowName = "sub_flow",
                Reason = "late-first-stop",
            },
            new WorkflowRunStoppedEvent
            {
                RunId = "child-run-1",
                Reason = "late-first-run-stop",
            },
            new StepCompletedEvent
            {
                RunId = "child-run-1",
                StepId = "step_1",
                Success = true,
                Output = "late-first-step",
            },
        };
        await eventStore.AppendAsync(
            ChildActorId,
            events.Select((evt, index) => StateEventFor(evt, index + 1)).ToList(),
            expectedVersion: 0);

        var replayed = CreateRunAgent(eventStore: eventStore);
        SetAgentId(replayed, ChildActorId);
        await replayed.ActivateAsync();

        replayed.State.RunId.Should().Be("child-run-2");
        replayed.State.Status.Should().Be("running");
        replayed.State.Input.Should().Be("second-input");
        replayed.State.FinalOutput.Should().BeEmpty();
        replayed.State.FinalError.Should().BeEmpty();
        replayed.State.ReusePolicy.Should().Be(WorkflowRunActorReusePolicy.SerialSingleton);
        replayed.State.BindingGeneration.Should().Be(2);
        replayed.State.ReuseAuthorityActorId.Should().Be(ParentActorId);
    }

    private static BindWorkflowRunDefinitionEvent SingletonBind(
        string runId,
        long bindingGeneration,
        string reuseAuthorityActorId = ParentActorId) =>
        new()
        {
            DefinitionActorId = "workflow-definition:sub_flow",
            WorkflowName = "sub_flow",
            WorkflowYaml = BuildValidWorkflowYaml("sub_role", "SubRole", workflowName: "sub_flow"),
            RunId = runId,
            DefinitionVersion = 1,
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            ReusePolicy = WorkflowRunActorReusePolicy.SerialSingleton,
            BindingGeneration = bindingGeneration,
            ReuseAuthorityActorId = reuseAuthorityActorId,
            InitialLineage = ChildLineage(reuseAuthorityActorId),
        };

    private static StartWorkflowEvent SingletonStart(string runId, long bindingGeneration) =>
        new()
        {
            WorkflowName = "sub_flow",
            RunId = runId,
            Input = $"input-{runId}",
            BindingGeneration = bindingGeneration,
            Parameters =
            {
                ["workflow_call.invocation_id"] = runId,
                ["workflow_call.lifecycle"] = "singleton",
            },
        };

    private static WorkflowRunLineage ChildLineage(string parentActorId) =>
        new()
        {
            Availability = WorkflowRunLineageAvailability.Available,
            RetryFork = new WorkflowRunRetryForkLineage
            {
                Availability = WorkflowRunLineageAvailability.Unavailable,
            },
            SubWorkflow = new WorkflowRunSubWorkflowLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                ParentRunId = "parent-run",
                ParentActorId = parentActorId,
                ParentStepId = "step-call",
                RootRunId = "parent-run",
                Depth = 1,
            },
        };

    private static Task DispatchAsync(
        WorkflowRunGAgent agent,
        IMessage message,
        string publisherActorId) =>
        agent.HandleEventAsync(Envelope(message, publisherActorId, TopologyAudience.Self));

    private static WorkflowExecutionKernelState KernelState(WorkflowRunGAgent agent) =>
        agent.State.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();

    private static StateEvent StateEventFor(IMessage evt, long version) =>
        new()
        {
            AgentId = ChildActorId,
            EventId = $"singleton-replay-{version}",
            EventType = evt.Descriptor.FullName,
            EventData = Google.Protobuf.WellKnownTypes.Any.Pack(evt),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Version = version,
        };
}
