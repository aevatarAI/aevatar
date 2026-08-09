using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
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
        var toolStep = planned.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        var admission = planned.State.ActiveTask.Gate.Admissions.Should().ContainSingle().Which;
        admission.Key.Should().BeEquivalentTo(toolStep.Operation.Key);
        admission.ToolCallId.Should().Be("call-alpha");
        admission.ToolName.Should().Be("repository_update");
        admission.ArgumentsSha256.Should().Equal(NyxIdChatPlanGateDecisions.HashArguments(arguments));
        toolStep.Status.Should().Be(NyxIdChatStepStatus.Planned);
        planned.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Postcondition &&
                step.DependsOn.Contains(toolStep.StepId))
            .Status.Should().Be(NyxIdChatStepStatus.Planned);

        var bytes = planned.State.ToByteArray();
        Encoding.UTF8.GetString(bytes).Should().NotContain("repo-secret-alpha");
        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(bytes);
        reloaded.ActiveTask.Gate.Should().BeEquivalentTo(planned.State.ActiveTask.Gate);
    }

    [Fact]
    public void WriteRequiredPolicy_ShouldRequireConfirmationWithoutDispatch_WhenSafetyApprovalIsFalse()
    {
        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            ActiveLlmState(),
            ToolPlan(
                "{\"repositoryId\":\"repo-alpha\"}",
                operationAdmission: ExactWriteAdmission()),
            Now);

        planned.NextCommand.Should().BeNull();
        planned.State.ActiveTask.Gate.Mode.Should().Be(NyxIdChatPlanGateMode.Confirm);
        planned.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Pending);
        planned.State.PendingApproval.Should().BeNull();
    }

    [Fact]
    public void ReadOnlyNonePolicyTool_ShouldAutoAdmitAndDispatchImmediately()
    {
        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            ActiveLlmState(),
            ToolPlan(
                "{}",
                isReadOnly: true,
                operationAdmission: ExactReadAdmission()),
            Now);

        planned.State.ActiveTask.Gate.Mode.Should().Be(NyxIdChatPlanGateMode.Auto);
        planned.State.ActiveTask.Gate.Status.Should().Be(NyxIdChatPlanGateStatus.Satisfied);
        planned.State.ActiveTask.Gate.RequestId.Should().BeEmpty();
        planned.State.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool)
            .Status.Should().Be(NyxIdChatStepStatus.Running);
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
        decision.State.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool)
            .Status.Should().Be(NyxIdChatStepStatus.Running);
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
    public void ExactConfirm_ConnectedServiceEffect_ShouldCarryFrozenAdmissionAndIdempotency()
    {
        var admission = ExactWriteAdmission();
        var state = NyxIdChatTaskLifecycle.ApplyOperationResult(
            ActiveLlmState(),
            ToolPlan(
                "{\"repositoryId\":\"repo-alpha\"}",
                requiresApproval: true,
                operationAdmission: admission),
            Now).State;
        var gate = state.ActiveTask.Gate;

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            state,
            ResolveCommand(gate, confirmed: true),
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.PlanGateContinuation.IdempotencyKey.Should()
            .Be(decision.NextCommand.Key.OperationId);
        decision.NextCommand.PlanGateContinuation.OperationAdmission.Should()
            .BeEquivalentTo(admission);
    }

    [Fact]
    public void DurableRetryConfirm_ShouldSealActorOwnedToolAuthority()
    {
        var state = DurableRetryPlanState();
        var command = ResolveCommand(state.ActiveTask.Gate, confirmed: true);

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            state,
            command,
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        var context = decision.NextCommand!.PlanGateContinuation.ToolContext;
        context.Request.RequestId.Should().Be(command.RequestId);
        context.Caller.ScopeId.Should().Be(state.ScopeId);
        context.Caller.OwnerScopeId.Should().Be(state.ScopeId);
        context.Caller.OwnerSubject.Should().Be(state.OwnerSubject);
        context.Caller.ResponseId.Should().Be(command.RequestId);
        context.Channel.Platform.Should().Be(NyxIdChatServiceDefaults.ServiceId);
        context.Channel.SenderId.Should().Be(state.OwnerSubject);
        context.Channel.RegistrationScopeId.Should().Be(state.ScopeId);
        context.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        context.ExecutionOwner.OwnerId.Should().Be(state.ConversationActorId);
    }

    [Theory]
    [InlineData("caller-owner")]
    [InlineData("channel-sender")]
    [InlineData("channel-scope")]
    [InlineData("channel-platform")]
    [InlineData("execution-owner")]
    public void DurableRetryConfirm_WithTamperedAuthority_ShouldFailClosed(string tamper)
    {
        var state = DurableRetryPlanState();
        var command = ResolveCommand(state.ActiveTask.Gate, confirmed: true);
        switch (tamper)
        {
            case "caller-owner":
                command.ToolContext.Caller.OwnerSubject = "owner-foreign";
                break;
            case "channel-sender":
                command.ToolContext.Channel.SenderId = "owner-foreign";
                break;
            case "channel-scope":
                command.ToolContext.Channel.RegistrationScopeId = "scope-foreign";
                break;
            case "channel-platform":
                command.ToolContext.Channel.Platform = "foreign-platform";
                break;
            case "execution-owner":
                command.ToolContext.ExecutionOwner.OwnerId = "conversation-foreign";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null);
        }

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            state,
            command,
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.NextCommand.Should().BeNull();
    }

    [Fact]
    public void DirectPostconditionGate_ShouldFreezeExactTypedAdmission()
    {
        var fixture = DirectPostconditionState();

        var gate = NyxIdChatPlanGateDecisions.BuildPostconditionGate(
            fixture.State,
            fixture.Step,
            fixture.Input);

        gate.Mode.Should().Be(NyxIdChatPlanGateMode.Confirm);
        gate.Status.Should().Be(NyxIdChatPlanGateStatus.Pending);
        gate.RequestId.Should().StartWith("plan-gate-");
        gate.TaskId.Should().Be("task-alpha");
        gate.PlanId.Should().Be("plan-alpha");
        gate.PlanRevision.Should().Be(3);
        var admission = gate.Admissions.Should().ContainSingle().Which;
        admission.Key.Should().BeEquivalentTo(fixture.Step.Operation.Key);
        admission.ActionRequestId.Should().Be(fixture.Input.ActionRequestId);
        admission.Action.Should().Be(NyxIdAssistantActionKind.ServiceConnect);
        admission.ActionParamsSha256.Should().Equal(
            NyxIdChatPlanGateDecisions.HashActionParams(fixture.Input.Params));
        admission.ActionPostcondition.Should().BeEquivalentTo(fixture.Input);
        fixture.State.PendingActions.Should().BeEmpty();
    }

    [Fact]
    public void ExactConfirm_DirectPostcondition_ShouldActivateWithoutPendingBrowserAction()
    {
        var fixture = DirectPostconditionState();
        fixture.State.ActiveTask.Gate = NyxIdChatPlanGateDecisions.BuildPostconditionGate(
            fixture.State,
            fixture.Step,
            fixture.Input);

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            fixture.State,
            ResolveCommand(fixture.State.ActiveTask.Gate, confirmed: true),
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.NextCommand.Should().NotBeNull();
        decision.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ActionPostcondition);
        decision.NextCommand.Key.Should().BeEquivalentTo(fixture.Step.Operation.Key);
        decision.NextCommand.ActionPostcondition.Should().BeEquivalentTo(fixture.Input);
        decision.State.PendingActions.Should().BeEmpty();
        var active = decision.State.ActiveTask.Steps.Single();
        active.Status.Should().Be(NyxIdChatStepStatus.Running);
        active.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        decision.State.ActiveTask.ActiveStepId.Should().Be(active.StepId);
        decision.State.ActiveTask.ActiveOperationId.Should().Be(
            active.Operation.Key.OperationId);
    }

    [Theory]
    [InlineData("operation-key")]
    [InlineData("postcondition")]
    [InlineData("action-kind")]
    [InlineData("action-request-id")]
    [InlineData("user-service-id")]
    [InlineData("typed-params")]
    [InlineData("params-hash")]
    public void DirectPostconditionConfirm_TamperedAdmission_ShouldNotAdvance(string tamper)
    {
        var fixture = DirectPostconditionState();
        fixture.State.ActiveTask.Gate = NyxIdChatPlanGateDecisions.BuildPostconditionGate(
            fixture.State,
            fixture.Step,
            fixture.Input);
        var admission = fixture.State.ActiveTask.Gate.Admissions.Single();
        switch (tamper)
        {
            case "operation-key":
                admission.Key.OperationId = "operation-foreign";
                break;
            case "postcondition":
                admission.ActionPostcondition = null;
                break;
            case "action-kind":
                admission.Action = NyxIdAssistantActionKind.ServiceReauthorize;
                admission.ActionPostcondition.Action =
                    NyxIdAssistantActionKind.ServiceReauthorize;
                break;
            case "action-request-id":
                admission.ActionRequestId = "action-foreign";
                admission.ActionPostcondition.ActionRequestId = "action-foreign";
                break;
            case "user-service-id":
                admission.ActionPostcondition.ResourceHint.UserService.UserServiceId =
                    "user-service-foreign";
                break;
            case "typed-params":
                admission.ActionPostcondition.Params.CustomServiceConnect =
                    new NyxIdCustomServiceConnectParams
                    {
                        Name = "foreign",
                        EndpointUrl = "https://example.invalid",
                    };
                admission.ActionParamsSha256 = NyxIdChatPlanGateDecisions.HashActionParams(
                    admission.ActionPostcondition.Params);
                break;
            case "params-hash":
                admission.ActionParamsSha256 = ByteString.CopyFrom(new byte[32]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null);
        }

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            fixture.State,
            ResolveCommand(fixture.State.ActiveTask.Gate, confirmed: true),
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeFalse();
        decision.NextCommand.Should().BeNull();
        decision.State.ActiveTask.Steps.Single().Status.Should().Be(
            NyxIdChatStepStatus.Planned);
    }

    [Fact]
    public void Reject_DirectPostcondition_ShouldCancelWithoutPendingBrowserAction()
    {
        var fixture = DirectPostconditionState();
        fixture.State.ActiveTask.Gate = NyxIdChatPlanGateDecisions.BuildPostconditionGate(
            fixture.State,
            fixture.Step,
            fixture.Input);

        var decision = NyxIdChatPlanGateDecisions.Resolve(
            fixture.State,
            ResolveCommand(fixture.State.ActiveTask.Gate, confirmed: false),
            currentStateVersion: 17,
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.NextCommand.Should().BeNull();
        var cancelled = decision.State.ActiveTask.Steps.Single();
        cancelled.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        cancelled.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Cancelled);
        cancelled.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        decision.State.PendingActions.Should().BeEmpty();
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
        decision.Result.Outcome.Should().Be(NyxIdChatControlOutcome.Rejected);
        decision.Result.ReasonCode.Should().Be(NyxIdChatControlCommands.PlanGatePending);
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
        var toolStep = decision.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        toolStep.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        toolStep.ExternalEffect.Should()
            .Be(NyxIdChatEffectEvidence.NotApplied);
        decision.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Postcondition &&
                step.DependsOn.Contains(toolStep.StepId))
            .Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        decision.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.TaskId == "task-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Stopped);
    }

    [Fact]
    public void ExpiredTurnAdmission_ShouldFailTaskAndCancelUnexecutedVerification()
    {
        var state = PendingPlanState();
        var sourceKey = state.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Llm && step.DependsOn.Count == 0).Operation.Key;
        var admission = NyxIdChatPlanGateDecisions.BuildTurnAdmission(state, sourceKey, Now);

        var decision = NyxIdChatPlanGateDecisions.ExpireCapability(
            state,
            new NyxIdChatPlanGateCapabilityExpiredSignal
            {
                Admission = admission!.Admission.Clone(),
                FailureCode = NyxIdChatTurnGAgent.PlanGateCapabilityExpiredCode,
                SafeMessage = "Re-plan from a safe checkpoint.",
            },
            Now);

        decision.ShouldCommit.Should().BeTrue();
        decision.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        decision.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        var toolStep = decision.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        toolStep.Status.Should().Be(NyxIdChatStepStatus.Failed);
        toolStep.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        decision.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Postcondition &&
                step.DependsOn.Contains(toolStep.StepId))
            .Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        decision.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Failed &&
            summary.FailureCode == NyxIdChatTurnGAgent.PlanGateCapabilityExpiredCode);
    }

    private static NyxIdChatConversationGAgentState PendingPlanState() =>
        NyxIdChatTaskLifecycle.ApplyOperationResult(
            ActiveLlmState(),
            ToolPlan("{\"repositoryId\":\"repo-alpha\"}", requiresApproval: true),
            Now).State;

    private static NyxIdChatConversationGAgentState DurableRetryPlanState()
    {
        var state = PendingPlanState();
        var step = state.ActiveTask.Steps.Single(candidate =>
            candidate.Kind == NyxIdChatStepKind.Tool);
        var sourceKey = step.Operation.Key.Clone();
        step.Operation.Key.OperationGeneration = 2;
        state.ActiveTask.Gate.Admissions.Single().Key = step.Operation.Key.Clone();
        step.RematerializeDurableAuthorization = true;
        step.RetryAuthorizationSourceKey = sourceKey;
        step.RetryToolInput = new NyxIdChatRetryToolInputState
        {
            Arguments = JsonParser.Default.Parse<Struct>("{\"repositoryId\":\"repo-alpha\"}"),
        };
        return state;
    }

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
        OwnerSubject = "owner-alpha",
        ToolContext = (AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(gate.RequestId, null),
            Credentials = new AgentToolCredentials(
                "fresh-user-token",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "owner-alpha",
                gate.RequestId,
                "scope-alpha"),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                "owner-alpha",
                "scope-alpha",
                null,
                null),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "owner-alpha",
                "proxy"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("conversation-alpha"),
        }).ToPayload(),
    };

    private static NyxIdChatOperationResultSignal ToolPlan(
        string arguments,
        bool requiresApproval = false,
        bool isReadOnly = false,
        AgentToolOperationAdmissionPayload? operationAdmission = null) => new()
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
                    OperationAdmission = operationAdmission?.Clone(),
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
            OwnerSubject = "owner-alpha",
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            ProgressSequence = 4,
            UpdatedAt = Now.Clone(),
        };
    }

    private static (
        NyxIdChatConversationGAgentState State,
        NyxIdChatTaskStepState Step,
        NyxIdChatActionPostconditionInput Input) DirectPostconditionState()
    {
        var key = OperationKey(
            "step-service-connect-postcondition",
            "operation-service-connect-postcondition");
        var step = new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 3,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    ActionRequestId = "action-service-connect-alpha",
                    EffectStepId = "step-require-service-alpha",
                    Check = "service.connected",
                    ProviderResourceId = "user-service-alpha",
                },
            },
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Requested,
            },
        };
        step.DependsOn.Add("step-require-service-alpha");
        var task = new NyxIdChatTaskState
        {
            TaskId = key.TaskId,
            TurnId = key.TurnId,
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = step.StepId,
            PlanId = "plan-alpha",
            PlanRevision = 3,
            CreatedAt = Now.Clone(),
            UpdatedAt = Now.Clone(),
        };
        task.Steps.Add(step);
        var turn = new NyxIdChatTurnState
        {
            TurnId = key.TurnId,
            TaskId = key.TaskId,
            Status = NyxIdChatTurnStatus.Active,
            Intent = NyxIdChatTurnIntent.ServiceConnect,
            CreatedAt = Now.Clone(),
        };
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = key.ConversationActorId,
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            ProgressSequence = 4,
            UpdatedAt = Now.Clone(),
        };
        var input = new NyxIdChatActionPostconditionInput
        {
            ScopeId = state.ScopeId,
            OwnerSubject = state.OwnerSubject,
            OriginTurnId = key.TurnId,
            ActionRequestId = step.Source.Postcondition.ActionRequestId,
            Action = NyxIdAssistantActionKind.ServiceConnect,
            ReportedDisposition = NyxIdChatActionDisposition.Completed,
            ResourceHint = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = step.Source.Postcondition.ProviderResourceId,
                },
            },
            Params = new NyxIdAssistantActionParams
            {
                CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
                {
                    ServiceSlug = "api-github",
                    RequestedScopes = { "repo" },
                },
            },
        };
        return (state, step, input);
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

    private static AgentToolOperationAdmissionPayload ExactWriteAdmission() => new()
    {
        ServiceInstanceId = "connected-service-alpha",
        ServiceSlug = "service-slug-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "endpoint-alpha",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "PATCH",
        PathTemplate = "/repositories/{repositoryId}",
        ContractDigest = new string('b', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.Write,
            Approval = AgentToolOperationApprovalPayload.Required,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes =
            {
                AgentToolOperationExecutionModePayload.Interactive,
            },
        },
    };

    private static AgentToolOperationAdmissionPayload ExactReadAdmission()
    {
        var admission = ExactWriteAdmission();
        admission.HttpMethod = "GET";
        admission.ExecutionPolicy.Risk = AgentToolOperationRiskPayload.ReadOnly;
        admission.ExecutionPolicy.Approval = AgentToolOperationApprovalPayload.None;
        return admission;
    }
}
