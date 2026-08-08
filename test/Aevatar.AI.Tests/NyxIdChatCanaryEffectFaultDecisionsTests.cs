using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatCanaryEffectFaultDecisionsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryArm_ShouldPersistExactOwnerBoundDirectiveAndRejectExactReplay()
    {
        var state = ConversationState();
        var command = ArmCommand();

        var armed = NyxIdChatCanaryEffectFaultDecisions.TryArm(
            state,
            command,
            stateVersion: 17,
            Timestamp.FromDateTimeOffset(Now),
            out var next);

        armed.Should().BeTrue();
        next.Should().NotBeSameAs(state);
        next.ProgressSequence.Should().Be(5);
        next.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
        next.CanaryEffectFault.Directive.OwnerSubject.Should().Be("owner-alpha");
        next.CanaryEffectFault.Directive.Key.Should().BeEquivalentTo(command.Key);

        var reloaded = NyxIdChatConversationGAgentState.Parser.ParseFrom(next.ToByteArray());
        NyxIdChatCanaryEffectFaultDecisions.TryArm(
                reloaded,
                command.Clone(),
                stateVersion: 18,
                Timestamp.FromDateTimeOffset(Now.AddSeconds(1)),
                out var replay)
            .Should().BeFalse("an exact arm is idempotent even after protobuf reload");
        replay.Should().BeSameAs(reloaded);
        reloaded.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("conversation")]
    [InlineData("generation")]
    [InlineData("service")]
    [InlineData("catalog")]
    [InlineData("version")]
    [InlineData("expired")]
    [InlineData("too_long")]
    public void TryArm_ShouldRejectMismatchedOrOutOfWindowDirective(string mismatch)
    {
        var state = ConversationState();
        var command = ArmCommand();
        var stateVersion = 17L;
        switch (mismatch)
        {
            case "owner":
                command.OwnerSubject = "owner-beta";
                break;
            case "conversation":
                command.Key.ConversationActorId = "conversation-beta";
                break;
            case "generation":
                command.Key.OperationGeneration = 2;
                break;
            case "service":
                command.ServiceInstanceId = string.Empty;
                break;
            case "catalog":
                command.CatalogDigest = "sha256:not-a-digest";
                break;
            case "version":
                stateVersion = 18;
                break;
            case "expired":
                command.ExpiresAt = Timestamp.FromDateTimeOffset(Now);
                break;
            case "too_long":
                command.ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(16));
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.TryArm(
                state,
                command,
                stateVersion,
                Timestamp.FromDateTimeOffset(Now),
                out var next)
            .Should().BeFalse();
        next.Should().BeSameAs(state);
        state.CanaryEffectFault.Should().BeNull();
    }

    [Theory]
    [InlineData("active_turn")]
    [InlineData("active_task")]
    [InlineData("gate")]
    [InlineData("gate_admission")]
    [InlineData("turn_admission")]
    [InlineData("tool_call")]
    [InlineData("tool_name")]
    [InlineData("arguments")]
    [InlineData("step")]
    [InlineData("duplicate_step")]
    [InlineData("stored_service")]
    [InlineData("stored_catalog")]
    [InlineData("stored_read_back")]
    public void TryArm_ShouldRejectAnyPendingActorFactMismatch(string mismatch)
    {
        var state = ConversationState();
        switch (mismatch)
        {
            case "active_turn":
                state.ActiveTurn.Status = NyxIdChatTurnStatus.Succeeded;
                break;
            case "active_task":
                state.ActiveTask.Status = NyxIdChatTaskStatus.Succeeded;
                break;
            case "gate":
                state.ActiveTask.Gate.Status = NyxIdChatPlanGateStatus.Satisfied;
                break;
            case "gate_admission":
                state.ActiveTask.Gate.Admissions[0].Key.OperationId = "operation-beta";
                break;
            case "turn_admission":
                state.ActiveTurnPlanGateAdmission.GateRequestId = "plan-gate-beta";
                break;
            case "tool_call":
                state.ActiveTurnPlanGateAdmission.ToolCallId = "call-beta";
                break;
            case "tool_name":
                state.ActiveTask.Steps[0].Source.Tool.ToolName = "tool-beta";
                break;
            case "arguments":
                state.ActiveTurnPlanGateAdmission.ArgumentsSha256 =
                    ByteString.CopyFrom(new byte[32].Select(static _ => (byte)1).ToArray());
                break;
            case "step":
                state.ActiveTask.Steps[0].Status = NyxIdChatStepStatus.Running;
                break;
            case "duplicate_step":
                state.ActiveTask.Steps.Add(state.ActiveTask.Steps[0].Clone());
                break;
            case "stored_service":
                state.ActiveTask.Steps[0].Source.Tool.OperationAdmission.ServiceInstanceId =
                    "connected-service-beta";
                break;
            case "stored_catalog":
                state.ActiveTask.Steps[0].Source.Tool.OperationAdmission.CatalogDigest =
                    $"sha256:{new string('b', 64)}";
                break;
            case "stored_read_back":
                state.ActiveTask.Steps[0].Source.Tool.OperationAdmission.ReadBack = null;
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.TryArm(
                state,
                ArmCommand(),
                stateVersion: 17,
                Timestamp.FromDateTimeOffset(Now),
                out var next)
            .Should().BeFalse();
        next.Should().BeSameAs(state);
        state.CanaryEffectFault.Should().BeNull();
    }

    [Fact]
    public void ForwardForPlanResolution_ShouldForwardOnlyExactAdmittedEffectOnce()
    {
        var state = Arm(ConversationState());
        var command = new NyxIdChatPlanResolveCommand
        {
            OwnerSubject = "owner-alpha",
        };
        var dispatch = EffectDispatch();

        var directive = NyxIdChatCanaryEffectFaultDecisions.ForwardForPlanResolution(
            state,
            command,
            dispatch,
            Timestamp.FromDateTimeOffset(Now.AddMinutes(1)));

        directive.Should().BeEquivalentTo(state.CanaryEffectFault.Directive);
        state.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Forwarded);
        state.CanaryEffectFault.ForwardedAt.Should().NotBeNull();
        state.CanaryEffectFault.ConsumedAt.Should().BeNull();
        NyxIdChatCanaryEffectFaultDecisions.ForwardForPlanResolution(
                state,
                command,
                dispatch,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().BeNull("the actor-owned directive is forwarded only once");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("key")]
    [InlineData("service")]
    [InlineData("catalog")]
    [InlineData("generation")]
    [InlineData("read_back")]
    [InlineData("tool_context_owner")]
    public void ForwardForPlanResolution_ShouldRejectAnyExactBindingMismatch(string mismatch)
    {
        var state = Arm(ConversationState());
        var command = new NyxIdChatPlanResolveCommand { OwnerSubject = "owner-alpha" };
        var dispatch = EffectDispatch();
        switch (mismatch)
        {
            case "owner":
                command.OwnerSubject = "owner-beta";
                break;
            case "key":
                dispatch.Key.OperationId = "operation-beta";
                break;
            case "service":
                dispatch.PlanGateContinuation.OperationAdmission.ServiceInstanceId =
                    "connected-service-beta";
                break;
            case "catalog":
                dispatch.PlanGateContinuation.OperationAdmission.CatalogDigest =
                    $"sha256:{new string('b', 64)}";
                break;
            case "generation":
                dispatch.Key.OperationGeneration = 2;
                break;
            case "read_back":
                dispatch.PlanGateContinuation.OperationAdmission.ReadBack = null;
                break;
            case "tool_context_owner":
                dispatch.PlanGateContinuation.ToolContext.Caller.OwnerSubject = "owner-beta";
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.ForwardForPlanResolution(
                state,
                command,
                dispatch,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().BeNull();
        state.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
    }

    [Fact]
    public void ForwardForPlanResolution_AfterExpiry_ShouldCommitExpiredStatus()
    {
        var state = Arm(ConversationState());

        NyxIdChatCanaryEffectFaultDecisions.ForwardForPlanResolution(
                state,
                new NyxIdChatPlanResolveCommand { OwnerSubject = "owner-alpha" },
                EffectDispatch(),
                Timestamp.FromDateTimeOffset(Now.AddMinutes(6)))
            .Should().BeNull();

        state.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Expired);
        state.CanaryEffectFault.ConsumedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("service")]
    [InlineData("catalog")]
    [InlineData("read_back")]
    [InlineData("generation")]
    public void MatchesTurnDispatch_ShouldFailClosedForAnyDirectiveMismatch(string mismatch)
    {
        var directive = Arm(ConversationState()).CanaryEffectFault.Directive.Clone();
        var dispatch = EffectDispatch();
        switch (mismatch)
        {
            case "owner":
                dispatch.PlanGateContinuation.ToolContext.Caller.OwnerSubject = "owner-beta";
                break;
            case "service":
                dispatch.PlanGateContinuation.OperationAdmission.ServiceInstanceId =
                    "connected-service-beta";
                break;
            case "catalog":
                dispatch.PlanGateContinuation.OperationAdmission.CatalogDigest =
                    $"sha256:{new string('b', 64)}";
                break;
            case "read_back":
                dispatch.PlanGateContinuation.OperationAdmission.ReadBack = null;
                break;
            case "generation":
                dispatch.Key.OperationGeneration = 2;
                break;
        }

        NyxIdChatCanaryEffectFaultDecisions.MatchesTurnDispatch(
                directive,
                dispatch,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().BeFalse();
    }

    [Fact]
    public void TryMarkConsumed_ShouldRequireExactTurnOwnedDispatchStartedAck()
    {
        var state = Arm(ConversationState());
        var dispatch = EffectDispatch();
        NyxIdChatCanaryEffectFaultDecisions.ForwardForPlanResolution(
                state,
                new NyxIdChatPlanResolveCommand { OwnerSubject = "owner-alpha" },
                dispatch,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().NotBeNull();
        var signal = new NyxIdChatCanaryEffectFaultConsumedSignal
        {
            ArmId = "arm-alpha",
            Key = dispatch.Key.Clone(),
            TurnActorId = NyxIdChatTurnActorIds.ForTurn("conversation-alpha", "turn-alpha"),
            ConsumedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(1)),
        };

        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                signal,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out var consumed)
            .Should().BeTrue();
        consumed.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Consumed);
        consumed.CanaryEffectFault.ConsumedAt.Should().Be(
            Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)));
        consumed.ProgressSequence.Should().Be(state.ProgressSequence + 1);

        var wrongTurn = signal.Clone();
        wrongTurn.TurnActorId = "turn-actor-beta";
        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                wrongTurn,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out _)
            .Should().BeFalse();
        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                consumed,
                signal,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1).AddSeconds(2)),
                out _)
            .Should().BeFalse("an exact consumed acknowledgement is idempotent");
    }

    [Fact]
    public void TryMarkConsumed_ShouldNotUseCrossActorClockAsProtocolOrdering()
    {
        var state = Arm(ConversationState());
        var dispatch = EffectDispatch();
        NyxIdChatCanaryEffectFaultDecisions.ForwardForPlanResolution(
                state,
                new NyxIdChatPlanResolveCommand { OwnerSubject = "owner-alpha" },
                dispatch,
                Timestamp.FromDateTimeOffset(Now.AddMinutes(1)))
            .Should().NotBeNull();
        var conversationNow = Timestamp.FromDateTimeOffset(Now.AddMinutes(1));
        var signal = new NyxIdChatCanaryEffectFaultConsumedSignal
        {
            ArmId = "arm-alpha",
            Key = dispatch.Key.Clone(),
            TurnActorId = NyxIdChatTurnActorIds.ForTurn("conversation-alpha", "turn-alpha"),
            ConsumedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-10)),
        };

        NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                state,
                signal,
                conversationNow,
                out var consumed)
            .Should().BeTrue();
        consumed.CanaryEffectFault.ConsumedAt.Should().Be(conversationNow);
    }

    private static NyxIdChatConversationGAgentState Arm(NyxIdChatConversationGAgentState state)
    {
        NyxIdChatCanaryEffectFaultDecisions.TryArm(
                state,
                ArmCommand(),
                stateVersion: 17,
                Timestamp.FromDateTimeOffset(Now),
                out var next)
            .Should().BeTrue();
        return next;
    }

    private static NyxIdChatConversationGAgentState ConversationState()
    {
        var key = OperationKey();
        var admission = WriteAdmission();
        admission.ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = ReadAdmission(),
            Arguments = new Struct(),
            CheckName = "resource-visible",
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Exists,
                JsonPointer = "/data",
            },
        };
        var argumentsSha256 = ByteString.CopyFrom(new byte[32]);
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            ProgressSequence = 4,
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = key.TurnId,
                TaskId = key.TaskId,
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TaskId = key.TaskId,
                TurnId = key.TurnId,
                Status = NyxIdChatTaskStatus.Active,
                PlanId = "plan-alpha",
                PlanRevision = 3,
                Gate = new NyxIdChatPlanGate
                {
                    Mode = NyxIdChatPlanGateMode.Confirm,
                    Status = NyxIdChatPlanGateStatus.Pending,
                    RequestId = "plan-gate-alpha",
                    TaskId = key.TaskId,
                    PlanId = "plan-alpha",
                    PlanRevision = 3,
                    Admissions =
                    {
                        new NyxIdChatPlanOperationAdmission
                        {
                            Key = key.Clone(),
                            ToolCallId = "call-alpha",
                            ToolName = "tool-alpha",
                            ArgumentsSha256 = argumentsSha256,
                        },
                    },
                },
            },
            ActiveTurnPlanGateAdmission = new NyxIdChatTurnPlanGateAdmissionState
            {
                Key = key.Clone(),
                GateRequestId = "plan-gate-alpha",
                TaskId = key.TaskId,
                PlanId = "plan-alpha",
                PlanRevision = 3,
                ToolCallId = "call-alpha",
                ToolName = "tool-alpha",
                ArgumentsSha256 = argumentsSha256,
                MayChangeExternalState = true,
                OperationAdmission = admission.Clone(),
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Planned,
            MayChangeExternalState = true,
            Operation = new NyxIdChatOperationState { Key = key.Clone() },
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = "tool-alpha",
                    OperationAdmission = admission,
                },
            },
        });
        return state;
    }

    private static NyxIdChatCanaryEffectFaultArmCommand ArmCommand() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        ArmId = "arm-alpha",
        ClientRequestId = "client-arm-alpha",
        Key = OperationKey(),
        ServiceInstanceId = "connected-service-alpha",
        CatalogDigest = $"sha256:{new string('a', 64)}",
        OwnerSubject = "owner-alpha",
        ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(5)),
        ExpectedStateVersion = 17,
    };

    private static NyxIdChatOperationDispatchCommand EffectDispatch()
    {
        var admission = WriteAdmission();
        admission.ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = ReadAdmission(),
            Arguments = new Struct(),
            CheckName = "resource-visible",
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Exists,
                JsonPointer = "/data",
            },
        };
        return new NyxIdChatOperationDispatchCommand
        {
            Key = OperationKey(),
            PlanGateContinuation = new NyxIdChatPlanGateContinuationInput
            {
                MayChangeExternalState = true,
                OperationAdmission = admission,
                ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
                {
                    Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
                    {
                        OwnerSubject = "owner-alpha",
                    },
                },
            },
        };
    }

    private static NyxIdChatOperationKey OperationKey() => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-effect-alpha",
        OperationId = "operation-effect-alpha",
        OperationGeneration = 1,
    };

    private static AgentToolOperationAdmissionPayload WriteAdmission() => new()
    {
        ServiceInstanceId = "connected-service-alpha",
        ServiceSlug = "service-slug-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "endpoint-effect-alpha",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "POST",
        PathTemplate = "/records",
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

    private static AgentToolOperationAdmissionPayload ReadAdmission() => new()
    {
        ServiceInstanceId = "connected-service-alpha",
        ServiceSlug = "service-slug-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "endpoint-read-alpha",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "GET",
        PathTemplate = "/records/{record_id}",
        ContractDigest = new string('c', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.ReadOnly,
            Approval = AgentToolOperationApprovalPayload.None,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes =
            {
                AgentToolOperationExecutionModePayload.Interactive,
                AgentToolOperationExecutionModePayload.Durable,
            },
        },
    };
}
