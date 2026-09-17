using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowRunGAgentSingletonArtifactFencingTests : WorkflowGAgentTestBase
{
    private const string ParentActorId = "workflow-run-parent-artifact-authority";
    private const string ChildActorId = "workflow-run-parent-artifact-authority:workflow:sub-flow";
    private const string RoleActorId = "workflow-run-parent-artifact-authority:workflow:sub-flow:role-a";
    private const string Run1 = "child-artifact-run-1";
    private const string Run2 = "child-artifact-run-2";

    [Fact]
    public async Task WorkflowRunGAgent_SerialSingleton_ShouldFencePriorRunCommittedArtifactsAndAcceptCurrentRun()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateSecondGenerationAgentAsync(eventStore);

        await ObserveAsync(agent, WorkflowCompletionPublication(
            "late-run1-workflow-completion",
            version: 101,
            runId: Run1,
            sessionId: "workflow-session-run1",
            content: "late workflow completion"));
        await ObserveAsync(agent, WorkflowCompletionPublication(
            "current-run2-workflow-completion",
            version: 102,
            runId: Run2,
            sessionId: "workflow-session-run2",
            content: "current workflow completion"));

        await ObserveAsync(agent, RoleCompletionPublication(
            "late-run1-role-completion",
            version: 103,
            runId: Run1,
            sessionId: "role-session-run1",
            content: "late role completion"));
        await ObserveAsync(agent, RoleCompletionPublication(
            "current-run2-role-completion",
            version: 104,
            runId: Run2,
            sessionId: "role-session-run2",
            content: "current role completion"));

        await ObserveAsync(agent, RoleProgressPublication(
            "late-run1-role-progress",
            version: 105,
            runId: Run1,
            sessionId: "progress-session-run1",
            operationId: "model-run1"));
        await ObserveAsync(agent, RoleProgressPublication(
            "current-run2-role-progress",
            version: 106,
            runId: Run2,
            sessionId: "progress-session-run2",
            operationId: "model-run2"));

        var persisted = await eventStore.GetEventsAsync(ChildActorId);
        var replies = persisted
            .Where(stateEvent => stateEvent.EventData.Is(WorkflowRoleReplyRecordedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<WorkflowRoleReplyRecordedEvent>())
            .ToList();
        var operations = persisted
            .Where(stateEvent => stateEvent.EventData.Is(WorkflowRuntimeOperationRecordedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<WorkflowRuntimeOperationRecordedEvent>())
            .ToList();

        replies.Should().HaveCount(2);
        replies.Should().OnlyContain(reply => reply.RunId == Run2);
        replies.Select(reply => reply.Content)
            .Should().BeEquivalentTo("current workflow completion", "current role completion");
        replies.Select(reply => reply.Source.CommittedEventId)
            .Should().BeEquivalentTo("current-run2-workflow-completion", "current-run2-role-completion");

        operations.Should().ContainSingle();
        operations[0].RunId.Should().Be(Run2);
        operations[0].OperationId.Should().Be("model-run2");
        operations[0].Source.CommittedEventId.Should().Be("current-run2-role-progress");

        agent.State.ProcessedArtifactSources.Select(source => source.CommittedEventId)
            .Should().BeEquivalentTo(
                "current-run2-workflow-completion",
                "current-run2-role-completion",
                "current-run2-role-progress");
        agent.State.ProcessedArtifactSources.Should().OnlyContain(source =>
            !source.CommittedEventId.Contains("run1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowRunGAgent_SerialSingleton_ShouldFailClosedWhenCommittedArtifactHasNoTypedRunId()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateSecondGenerationAgentAsync(eventStore);
        var persistedBefore = (await eventStore.GetEventsAsync(ChildActorId)).Count;

        await ObserveAsync(agent, WorkflowCompletionPublication(
            "untyped-workflow-completion",
            version: 201,
            runId: string.Empty,
            sessionId: "untyped-workflow-session",
            content: "must not persist"));
        await ObserveAsync(agent, RoleCompletionPublication(
            "untyped-role-completion",
            version: 202,
            runId: string.Empty,
            sessionId: "untyped-role-session",
            content: "must not persist"));
        await ObserveAsync(agent, RoleProgressPublication(
            "untyped-role-progress",
            version: 203,
            runId: string.Empty,
            sessionId: "untyped-progress-session",
            operationId: "untyped-model"));

        var persisted = await eventStore.GetEventsAsync(ChildActorId);
        persisted.Should().HaveCount(persistedBefore);
        persisted.Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(WorkflowRoleReplyRecordedEvent.Descriptor) ||
            stateEvent.EventData.Is(WorkflowRuntimeOperationRecordedEvent.Descriptor));
        agent.State.ProcessedArtifactSources.Should().BeEmpty();
        agent.State.ProcessedArtifactStateVersionsByPublisher.Should().BeEmpty();
    }

    private static async Task<WorkflowRunGAgent> CreateSecondGenerationAgentAsync(InMemoryEventStore eventStore)
    {
        var agent = CreateRunAgent(eventStore: eventStore);
        SetAgentId(agent, ChildActorId);

        await DispatchAsync(agent, SingletonBind(Run1, bindingGeneration: 1));
        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "sub_flow",
            RunId = Run1,
            Success = true,
            Output = "run1 completed",
        });
        await DispatchAsync(agent, SingletonBind(Run2, bindingGeneration: 2));

        agent.State.RunId.Should().Be(Run2);
        agent.State.ReusePolicy.Should().Be(WorkflowRunActorReusePolicy.SerialSingleton);
        agent.State.BindingGeneration.Should().Be(2);
        agent.State.ProcessedArtifactSources.Should().BeEmpty();
        return agent;
    }

    private static BindWorkflowRunDefinitionEvent SingletonBind(string runId, long bindingGeneration) =>
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
            ReuseAuthorityActorId = ParentActorId,
            InitialLineage = ChildLineage(),
        };

    private static WorkflowRunLineage ChildLineage() =>
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
                ParentActorId = ParentActorId,
                ParentStepId = "step-call",
                RootRunId = "parent-run",
                Depth = 1,
            },
        };

    private static Task DispatchAsync(WorkflowRunGAgent agent, IMessage message) =>
        agent.HandleEventAsync(Envelope(message, ParentActorId, TopologyAudience.Self));

    private static Task ObserveAsync(WorkflowRunGAgent agent, CommittedStateEventPublished publication) =>
        agent.HandleWorkflowArtifactObservationEnvelope(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RoleActorId),
            Payload = Any.Pack(publication),
        });

    private static CommittedStateEventPublished WorkflowCompletionPublication(
        string eventId,
        long version,
        string runId,
        string sessionId,
        string content) =>
        CommittedPublication(
            eventId,
            version,
            new WorkflowLlmInvocationCompletedEvent
            {
                RunId = runId,
                StepId = "llm-step",
                SessionId = sessionId,
                RoleActorId = RoleActorId,
                Success = true,
                Content = content,
            });

    private static CommittedStateEventPublished RoleCompletionPublication(
        string eventId,
        long version,
        string runId,
        string sessionId,
        string content) =>
        CommittedPublication(
            eventId,
            version,
            new RoleChatSessionCompletedEvent
            {
                SessionId = sessionId,
                RoleId = "sub_role",
                Content = content,
                ContentEmitted = true,
                Outcome = RoleChatSessionOutcome.Completed,
                WorkflowLlmCompletionDeliveryContext = string.IsNullOrWhiteSpace(runId)
                    ? null
                    : CompletionContext(runId, sessionId),
            });

    private static CommittedStateEventPublished RoleProgressPublication(
        string eventId,
        long version,
        string runId,
        string sessionId,
        string operationId)
    {
        var roleState = new RoleGAgentState();
        if (!string.IsNullOrWhiteSpace(runId))
        {
            roleState.Sessions[sessionId] = new RoleChatSessionState
            {
                WorkflowLlmCompletionDeliveryContext = CompletionContext(runId, sessionId),
            };
        }

        return CommittedPublication(
            eventId,
            version,
            new RoleChatSessionProgressedEvent
            {
                SessionId = sessionId,
                Sequence = version,
                ModelStarted = new RoleChatModelStartedProgress
                {
                    OperationId = operationId,
                    Round = 1,
                    Model = "model-a",
                    Provider = "provider-a",
                },
            },
            roleState);
    }

    private static WorkflowLlmCompletionDeliveryContext CompletionContext(string runId, string sessionId) =>
        new()
        {
            RunId = runId,
            StepId = "llm-step",
            SessionId = sessionId,
        };

    private static CommittedStateEventPublished CommittedPublication(
        string eventId,
        long version,
        IMessage committedEvent,
        RoleGAgentState? stateRoot = null)
    {
        var publication = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                AgentId = RoleActorId,
                EventId = eventId,
                EventType = committedEvent.Descriptor.FullName,
                EventData = Any.Pack(committedEvent),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = version,
            },
        };
        if (stateRoot != null)
            publication.StateRoot = Any.Pack(stateRoot);

        return publication;
    }
}
