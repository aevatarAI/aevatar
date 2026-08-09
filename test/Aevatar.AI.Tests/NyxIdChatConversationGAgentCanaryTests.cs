using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatConversationGAgentTests
{
    private static readonly DateTimeOffset CanaryNow =
        new(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CanaryEffectFault_ShouldRecoverActorOwnedForwardAndConsumptionState()
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, actorId, 1, CanaryConversationState());
        using var services = BuildEventSourcingServices(eventStore);
        var firstDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var first = CreateController(
            services,
            actorId,
            firstDispatch,
            timeProvider: new FixedTimeProvider(CanaryNow));
        await first.ActivateAsync();
        var arm = CanaryArmCommand(expectedStateVersion: 1);

        await first.HandleEventAsync(CreateEnvelope(actorId, arm));

        first.State.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2);

        var armed = CreateController(
            services,
            actorId,
            firstDispatch,
            timeProvider: new FixedTimeProvider(CanaryNow.AddSeconds(1)));
        await armed.ActivateAsync();
        armed.State.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
        armed.State.CanaryEffectFault.Directive.Should().BeEquivalentTo(
            first.State.CanaryEffectFault.Directive);

        await armed.HandleEventAsync(CreateEnvelope(actorId, arm.Clone()));
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2,
            "the exact arm replay must not create another committed event");

        var resolve = new NyxIdChatPlanResolveCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = actorId,
            TaskId = "task-alpha",
            PlanId = "plan-alpha",
            PlanRevision = 3,
            RequestId = "plan-gate-alpha",
            ClientRequestId = "client-plan-alpha",
            Confirmed = true,
            ExpectedStateVersion = 2,
            CommandId = "command-plan-alpha",
            CorrelationId = "correlation-plan-alpha",
            OwnerSubject = "owner-alpha",
            ToolContext = new AgentToolExecutionContextPayload
            {
                Caller = new AgentToolCallerContextPayload
                {
                    ScopeId = "scope-alpha",
                    OwnerSubject = "owner-alpha",
                    ResponseId = "plan-gate-alpha",
                },
            },
        };
        await armed.HandleEventAsync(CreateEnvelope(actorId, resolve));

        armed.State.CanaryEffectFault.Status.Should().Be(
            NyxIdChatCanaryEffectFaultStatus.Forwarded);
        armed.State.CanaryEffectFault.ForwardedAt.Should().NotBeNull();
        armed.State.CanaryEffectFault.ConsumedAt.Should().BeNull();
        var forwarded = firstDispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        forwarded.PlanGateContinuation.CanaryEffectFault.Should().BeEquivalentTo(
            armed.State.CanaryEffectFault.Directive);

        var forwardedReload = CreateController(
            services,
            actorId,
            timeProvider: new FixedTimeProvider(CanaryNow.AddSeconds(2)));
        await forwardedReload.ActivateAsync();
        forwardedReload.State.CanaryEffectFault.Status.Should().Be(
            NyxIdChatCanaryEffectFaultStatus.Forwarded);

        var eventsBeforeAmbiguousResult = (await eventStore.GetEventsAsync(actorId)).Count;
        await forwardedReload.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatOperationResultSignal
            {
                Key = CanaryOperationKey(),
                Failure = new NyxIdChatOperationFailure
                {
                    FailureCode = NyxIdChatTurnGAgent.CanaryEffectFaultCode,
                    SafeMessage = "The external operation may have changed state and requires exact read-back.",
                    ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
                },
            }));

        forwardedReload.State.CanaryEffectFault.Status.Should().Be(
            NyxIdChatCanaryEffectFaultStatus.Forwarded,
            "the result can become durable before the consumed acknowledgement retry arrives");
        var ambiguousReconciliation = (await eventStore.GetEventsAsync(actorId))
            .Skip(eventsBeforeAmbiguousResult)
            .Where(item => item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Select(item => item.EventData.Unpack<NyxIdChatOperationReconciledEvent>())
            .Should().ContainSingle().Which;
        ambiguousReconciliation.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnGAgent.CanaryEffectFaultCode);
        ambiguousReconciliation.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);
        firstDispatch.OperationCalls.Should().ContainSingle(
            "processing the ambiguous result must not execute the effect again");

        var consumedSignal = new NyxIdChatCanaryEffectFaultConsumedSignal
        {
            ArmId = "arm-alpha",
            Key = CanaryOperationKey(),
            TurnActorId = NyxIdChatTurnActorIds.ForTurn(actorId, "turn-alpha"),
            ConsumedAt = Timestamp.FromDateTimeOffset(CanaryNow.AddMinutes(-5)),
            ServiceInstanceId = "connected-service-alpha",
            ApprovalRequestId = "approval-7001-alpha",
            ReceiptStatus = AgentToolReceiptStatus.Denied,
            ApprovalDecisionMode = NyxIdApprovalDecisionMode.Unspecified,
            ApprovalTerminalOutcome = NyxIdApprovalTerminalOutcome.Rejected,
            ApprovalSubjectKind = "nyxid.user-service",
            ApprovalSubjectId = "connected-service-alpha",
            ApprovalCallId = "call-alpha",
            ApprovalToolName = "tool-alpha",
        };
        await forwardedReload.HandleEventAsync(CreateEnvelope(actorId, consumedSignal));

        forwardedReload.State.CanaryEffectFault.Status.Should().Be(
            NyxIdChatCanaryEffectFaultStatus.Consumed);
        forwardedReload.State.CanaryEffectFault.ConsumedAt.ToDateTimeOffset().Should().Be(
            CanaryNow.AddSeconds(2));
        forwardedReload.State.CanaryEffectFault.ApprovalRequestId.Should().Be(
            "approval-7001-alpha");
        forwardedReload.State.CanaryEffectFault.ReceiptStatus.Should().Be(
            AgentToolReceiptStatus.Denied);
        forwardedReload.State.CanaryEffectFault.ApprovalDecisionMode.Should().Be(
            NyxIdApprovalDecisionMode.Unspecified);
        forwardedReload.State.CanaryEffectFault.ApprovalTerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        var consumedStep = forwardedReload.State.ActiveTask.Steps.Should().ContainSingle().Which;
        consumedStep.ApprovalRequestId.Should().Be("approval-7001-alpha");
        consumedStep.ApprovalObservation.Should().NotBeNull();
        consumedStep.ApprovalObservation.ApprovalRequestId.Should().Be("approval-7001-alpha");
        consumedStep.ApprovalObservation.ReceiptStatus.Should().Be(AgentToolReceiptStatus.Denied);
        consumedStep.ApprovalObservation.DecisionMode.Should().Be(
            NyxIdApprovalDecisionMode.Unspecified);
        consumedStep.ApprovalObservation.TerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        consumedStep.ApprovalObservation.SubjectKind.Should().Be("nyxid.user-service");
        consumedStep.ApprovalObservation.SubjectId.Should().Be("connected-service-alpha");
        var eventsAfterConsumed = (await eventStore.GetEventsAsync(actorId)).Count;

        await forwardedReload.HandleEventAsync(CreateEnvelope(actorId, consumedSignal.Clone()));

        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(eventsAfterConsumed,
            "a duplicate reminder or activation retry must not commit consumption twice");

        var consumedReload = CreateController(
            services,
            actorId,
            timeProvider: new FixedTimeProvider(CanaryNow.AddSeconds(3)));
        await consumedReload.ActivateAsync();
        consumedReload.State.CanaryEffectFault.Status.Should().Be(
            NyxIdChatCanaryEffectFaultStatus.Consumed);
        consumedReload.State.CanaryEffectFault.ConsumedAt.Should().Be(
            forwardedReload.State.CanaryEffectFault.ConsumedAt);
        consumedReload.State.CanaryEffectFault.ApprovalRequestId.Should().Be(
            "approval-7001-alpha");
        var reloadedStep = consumedReload.State.ActiveTask.Steps.Should().ContainSingle().Which;
        reloadedStep.ApprovalRequestId.Should().Be("approval-7001-alpha");
        reloadedStep.ApprovalObservation.TerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        firstDispatch.OperationCalls.Should().ContainSingle(
            "the delayed acknowledgement is an idempotent state transition, not a redispatch");
    }

    private static NyxIdChatConversationGAgentState CanaryConversationState()
    {
        var key = CanaryOperationKey();
        var admission = CanaryWriteAdmission();
        var argumentsSha256 = ByteString.CopyFrom(new byte[32]);
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
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
                AdmittedAt = Timestamp.FromDateTimeOffset(CanaryNow.AddMinutes(-1)),
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Planned,
            MayChangeExternalState = true,
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = NyxIdChatStepKind.Tool,
            },
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = "tool-alpha",
                    OperationAdmission = admission,
                },
            },
            RetryToolInput = new NyxIdChatRetryToolInputState
            {
                CallId = "call-alpha",
                ToolName = "tool-alpha",
                Arguments = new Struct(),
                OperationAdmission = admission.Clone(),
            },
        });
        return state;
    }

    private static NyxIdChatCanaryEffectFaultArmCommand CanaryArmCommand(
        long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        ArmId = "arm-alpha",
        ClientRequestId = "client-arm-alpha",
        Key = CanaryOperationKey(),
        ServiceInstanceId = "connected-service-alpha",
        OwnerSubject = "owner-alpha",
        ExpiresAt = Timestamp.FromDateTimeOffset(CanaryNow.AddMinutes(5)),
        ExpectedStateVersion = expectedStateVersion,
        CommandId = "command-arm-alpha",
        CorrelationId = "correlation-arm-alpha",
    };

    private static NyxIdChatOperationKey CanaryOperationKey() => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-effect-alpha",
        OperationId = "operation-effect-alpha",
        OperationGeneration = 1,
    };

    private static AgentToolOperationAdmissionPayload CanaryWriteAdmission()
    {
        var admission = new AgentToolOperationAdmissionPayload
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
        admission.ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = new AgentToolOperationAdmissionPayload
            {
                ServiceInstanceId = admission.ServiceInstanceId,
                ServiceSlug = admission.ServiceSlug,
                PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
                {
                    EndpointId = "endpoint-read-alpha",
                },
                AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
                HttpMethod = "GET",
                PathTemplate = "/records/{record_id}",
                ContractDigest = new string('c', 64),
                CatalogDigest = admission.CatalogDigest,
                ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
                {
                    Risk = AgentToolOperationRiskPayload.ReadOnly,
                    Approval = AgentToolOperationApprovalPayload.None,
                    EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
                    AllowedExecutionModes =
                    {
                        AgentToolOperationExecutionModePayload.Interactive,
                    },
                },
            },
            Arguments = new Struct(),
            CheckName = "resource-visible",
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Exists,
                JsonPointer = "/data",
            },
        };
        return admission;
    }
}
