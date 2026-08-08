using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatPlanGateDecisionsTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 8, 8, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void ApprovalRequiredTool_ShouldPersistPendingExactGateWithoutDispatchOrArguments()
    {
        var state = ActiveLlmState();
        const string arguments = "{\"repositoryId\":\"repo-secret-alpha\"}";

        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            ToolPlan(arguments, requiresApproval: true),
            Now);

        planned.NextCommand.Should().BeNull();
        planned.State.ActiveTask.Gate.Mode.Should().Be(NyxIdChatPlanGateMode.Confirm);
        planned.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Pending);
        planned.State.ActiveTask.Gate.RequestId.Should().StartWith("plan-gate-");
        planned.State.ActiveTask.Gate.TaskId.Should().Be("task-alpha");
        planned.State.ActiveTask.Gate.PlanId.Should().Be("plan-alpha");
        planned.State.ActiveTask.Gate.PlanRevision.Should().Be(2);
        var admission = planned.State.ActiveTask.Gate.Admissions.Should().ContainSingle().Which;
        admission.Key.Should().BeEquivalentTo(planned.State.ActiveTask.Steps.Last().Operation.Key);
        admission.ToolCallId.Should().Be("call-alpha");
        admission.ToolName.Should().Be("repository_update");
        admission.ArgumentsSha256.Should().Equal(NyxIdChatPlanGateDecisions.HashArguments(arguments));
        planned.State.ActiveTask.Steps.Last().Status.Should().Be(NyxIdChatStepStatus.Planned);

        var bytes = planned.State.ToByteArray();
        Encoding.UTF8.GetString(bytes).Should().NotContain("repo-secret-alpha");
        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(bytes);
        reloaded.ActiveTask.Gate.Should().BeEquivalentTo(planned.State.ActiveTask.Gate);
    }

    [Fact]
    public void ReadOnlyTool_ShouldAutoAdmitAndDispatchImmediately()
    {
        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            ActiveLlmState(),
            ToolPlan("{}", isReadOnly: true),
            Now);

        planned.State.ActiveTask.Gate.Mode.Should().Be(NyxIdChatPlanGateMode.Auto);
        planned.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Satisfied);
        planned.State.ActiveTask.Gate.RequestId.Should().BeEmpty();
        planned.State.ActiveTask.Steps.Last().Status.Should().Be(NyxIdChatStepStatus.Running);
        planned.NextCommand.Should().NotBeNull();
        planned.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
    }

    [Fact]
    public void EstimatedPlanAboveConfiguredThreshold_ShouldRequireConfirmation()
    {
        var state = ActiveLlmState();
        state.ActiveTask.Steps[0].Estimate = new NyxIdChatStepEstimate
        {
            Kind = NyxIdChatStepEstimateKind.Duration,
            Seconds = 601,
        };

        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            ToolPlan("{}", isReadOnly: true),
            Now,
            planGateConfirmationThresholdSeconds: 600);

        planned.NextCommand.Should().BeNull();
        planned.State.ActiveTask.Gate.Mode.Should().Be(NyxIdChatPlanGateMode.Confirm);
        planned.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Pending);
    }

    [Fact]
    public void ExactConfirm_ShouldCommitSatisfiedGateAndActivateOnlyBoundOperation()
    {
        var state = PendingPlanState();
        var gate = state.ActiveTask.Gate;

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            state,
            ResolveCommand(gate, confirmed: true),
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.IsExactReplay.Should().BeFalse();
        decision.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Satisfied);
        decision.State.ActiveTask.Gate.DecidedAt.Should().Be(Now);
        decision.State.ActiveTask.Steps.Last().Status.Should().Be(NyxIdChatStepStatus.Running);
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.PlanGateContinuation);
        decision.NextCommand.Key.Should().BeEquivalentTo(gate.Admissions.Single().Key);
        decision.NextCommand.PlanGateContinuation.PlanId.Should().Be("plan-alpha");
        decision.NextCommand.PlanGateContinuation.ArgumentsSha256.Should()
            .Equal(gate.Admissions.Single().ArgumentsSha256);
        decision.State.RecentPlanResolutions.Should().ContainSingle();
    }

    [Fact]
    public void StaleOrWrongPlanConfirm_ShouldNotAdvance()
    {
        var state = PendingPlanState();
        var original = state.Clone();
        var gate = state.ActiveTask.Gate;

        var stale = NyxIdChatPlanGateDecisions.Resolve(
            state,
            ResolveCommand(gate, confirmed: true),
            currentStateVersion: 18,
            Now);
        var wrongRevisionCommand = ResolveCommand(gate, confirmed: true);
        wrongRevisionCommand.PlanRevision++;
        var wrongRevision = NyxIdChatPlanGateDecisions.Resolve(
            state,
            wrongRevisionCommand,
            currentStateVersion: 17,
            Now);
        var wrongPlanIdCommand = ResolveCommand(gate, confirmed: true);
        wrongPlanIdCommand.PlanId = "plan-foreign";
        var wrongPlanId = NyxIdChatPlanGateDecisions.Resolve(
            state,
            wrongPlanIdCommand,
            currentStateVersion: 17,
            Now);

        stale.ShouldCommit.Should().BeFalse();
        stale.NextCommand.Should().BeNull();
        wrongRevision.ShouldCommit.Should().BeFalse();
        wrongRevision.NextCommand.Should().BeNull();
        wrongPlanId.ShouldCommit.Should().BeFalse();
        wrongPlanId.NextCommand.Should().BeNull();
        state.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void SteeringPendingPlan_ShouldNotSatisfyGateOrChangePlanRevision()
    {
        var state = PendingPlanState();
        var gate = state.ActiveTask.Gate.Clone();
        var revision = state.ActiveTask.PlanRevision;
        var revisionHistory = state.ActiveTask.PlanRevisions
            .Select(static record => record.ToByteString().ToBase64())
            .ToArray();

        var decision = NyxIdChatControlCommands.Steer(
            state,
            new NyxIdChatSteeringCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-alpha",
                SteeringId = "steering-alpha",
                ClientRequestId = "client-steering-alpha",
                Instruction = "Keep the same plan but summarize progress.",
                ExpectedStateVersion = 17,
            },
            stateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.State.ActiveTask.Gate.Should().BeEquivalentTo(gate);
        decision.State.ActiveTask.Gate.Status.Should().Be(
            NyxIdChatPlanGateStatus.Pending);
        decision.State.ActiveTask.PlanRevision.Should().Be(revision);
        decision.State.ActiveTask.PlanRevisions
            .Select(static record => record.ToByteString().ToBase64())
            .Should().Equal(revisionHistory);
    }

    [Fact]
    public void Reject_ShouldStopTaskWithoutDispatchAndRecordTerminalSummary()
    {
        var state = PendingPlanState();

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            state,
            ResolveCommand(state.ActiveTask.Gate, confirmed: false),
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.NextCommand.Should().BeNull();
        decision.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Rejected);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        decision.State.ActiveTask.Steps.Last().Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        decision.State.ActiveTask.Steps.Last().ExternalEffect.Should()
            .Be(NyxIdChatEffectEvidence.NotApplied);
        decision.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.TaskId == "task-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Stopped);
    }

    private static NyxIdChatConversationGAgentState PendingPlanState() =>
        NyxIdChatTaskLifecycle.ApplyOperationResult(
            ActiveLlmState(),
            ToolPlan("{\"repositoryId\":\"repo-alpha\"}", requiresApproval: true),
            Now).State;

    private static NyxIdChatPlanResolveCommand ResolveCommand(
        NyxIdChatPlanGate gate,
        bool confirmed) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TaskId = gate.TaskId,
        PlanId = gate.PlanId,
        PlanRevision = gate.PlanRevision,
        RequestId = gate.RequestId,
        ClientRequestId = confirmed ? "confirm-alpha" : "reject-alpha",
        Confirmed = confirmed,
        ExpectedStateVersion = 17,
        ToolContext = new AgentToolExecutionContextPayload
        {
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "fresh-user-token",
                NyxIdCredentialKind =
                    AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
            },
        },
    };

    private static NyxIdChatOperationResultSignal ToolPlan(
        string arguments,
        bool requiresApproval = false,
        bool isReadOnly = false) => new()
    {
        Key = OperationKey("step-llm-alpha", "operation-llm-alpha"),
        Llm = new NyxIdChatLLMOperationResult
        {
            Content = "I will run the disclosed operation after the plan gate.",
            ToolCalls =
            {
                new NyxIdChatToolCall
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    ArgumentsJson = arguments,
                    Safety = new NyxIdChatToolCallSafety
                    {
                        IsReadOnly = isReadOnly,
                        RequiresApproval = requiresApproval,
                        MayChangeExternalState = !isReadOnly,
                    },
                },
            },
        },
    };

    private static NyxIdChatConversationGAgentState ActiveLlmState()
    {
        var step = new NyxIdChatTaskStepState
        {
            StepId = "step-llm-alpha",
            Order = 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Operation = new NyxIdChatOperationState
            {
                Key = OperationKey("step-llm-alpha", "operation-llm-alpha"),
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
        };
        var task = new NyxIdChatTaskState
        {
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = step.StepId,
            ActiveOperationId = step.Operation.Key.OperationId,
            PlanId = "plan-alpha",
            PlanRevision = 1,
            CreatedAt = Now.Clone(),
            UpdatedAt = Now.Clone(),
        };
        task.Steps.Add(step);
        var turn = new NyxIdChatTurnState
        {
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            Status = NyxIdChatTurnStatus.Active,
            CreatedAt = Now.Clone(),
        };
        return new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            ProgressSequence = 4,
            UpdatedAt = Now.Clone(),
        };
    }

    private static NyxIdChatOperationKey OperationKey(string stepId, string operationId) => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = stepId,
        OperationId = operationId,
        OperationGeneration = 1,
    };
}
