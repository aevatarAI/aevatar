using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatConversationGAgentTests
{
    private static readonly DateTimeOffset CanaryNow =
        new(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CanaryEffectFault_ShouldRecoverDirectToolForwardAndConsumptionState()
    {
        const string actorId = "conversation-alpha";
        var sourceOperationKey = CanarySourceLlmOperationKey();
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, actorId, 1, CanaryConversationState());
        using var services = BuildEventSourcingServices(eventStore);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var first = CreateController(
            services,
            actorId,
            dispatch,
            timeProvider: new FixedTimeProvider(CanaryNow));
        await first.ActivateAsync();
        var arm = CanaryArmCommand(expectedStateVersion: 1);

        await first.HandleEventAsync(CreateEnvelope(actorId, arm));

        first.State.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
        first.State.CanaryEffectFault.ArmIntent.SourceOperationKey.Should().BeEquivalentTo(
            sourceOperationKey);
        first.State.CanaryEffectFault.Directive.Should().BeNull();
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2);

        var armed = CreateController(
            services,
            actorId,
            dispatch,
            timeProvider: new FixedTimeProvider(CanaryNow.AddSeconds(1)));
        await armed.ActivateAsync();
        armed.State.CanaryEffectFault.Status.Should().Be(NyxIdChatCanaryEffectFaultStatus.Armed);
        armed.State.CanaryEffectFault.ArmIntent.Should().BeEquivalentTo(
            first.State.CanaryEffectFault.ArmIntent);
        armed.State.CanaryEffectFault.Directive.Should().BeNull();

        await armed.HandleEventAsync(CreateEnvelope(actorId, arm.Clone()));
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2,
            "the exact arm replay must not create another committed event");

        var admission = CanaryWriteAdmission();
        await armed.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatOperationResultSignal
            {
                Key = sourceOperationKey.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = "Dispatch the admitted effect.",
                    ToolCalls =
                    {
                        new NyxIdChatToolCall
                        {
                            CallId = "call-alpha",
                            ToolName = "tool-alpha",
                            ArgumentsJson = "{\"value\":1}",
                            Safety = new NyxIdChatToolCallSafety
                            {
                                IsReadOnly = false,
                                IsDestructive = false,
                                MayChangeExternalState = true,
                                SideEffectKind = "records.create",
                            },
                            OperationAdmission = admission.Clone(),
                        },
                    },
                },
            }));

        var forwarded = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        forwarded.InputCase.Should().Be(NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        forwarded.Key.Should().NotBeEquivalentTo(sourceOperationKey);
        forwarded.Key.OperationGeneration.Should().Be(1);
        forwarded.Tool.CallId.Should().Be("call-alpha");
        forwarded.Tool.ToolName.Should().Be("tool-alpha");
        forwarded.Tool.OperationAdmission.Should().BeEquivalentTo(admission);
        forwarded.Tool.CanaryEffectFault.Should().NotBeNull();
        forwarded.Tool.CanaryEffectFault.Should().BeEquivalentTo(
            armed.State.CanaryEffectFault.Directive);
        forwarded.Tool.CanaryEffectFault.Key.Should().BeEquivalentTo(forwarded.Key);
        armed.State.CanaryEffectFault.Status.Should().Be(
            NyxIdChatCanaryEffectFaultStatus.Forwarded);
        armed.State.CanaryEffectFault.ForwardedAt.Should().NotBeNull();
        armed.State.CanaryEffectFault.ConsumedAt.Should().BeNull();
        var forwardedStep = CanaryTargetToolStep(armed.State);
        forwardedStep.Operation.Key.Should().BeEquivalentTo(forwarded.Key);
        forwardedStep.Source.Tool.OperationAdmission.Should().BeEquivalentTo(admission);
        forwardedStep.RetryToolInput.Should().NotBeNull();
        forwardedStep.RetryToolInput.CallId.Should().Be("call-alpha");
        forwardedStep.RetryToolInput.ToolName.Should().Be("tool-alpha");
        forwardedStep.RetryToolInput.OperationAdmission.Should().BeEquivalentTo(admission);

        var forwardedReload = CreateController(
            services,
            actorId,
            timeProvider: new FixedTimeProvider(CanaryNow.AddSeconds(2)));
        await forwardedReload.ActivateAsync();
        forwardedReload.State.CanaryEffectFault.Status.Should().Be(
            NyxIdChatCanaryEffectFaultStatus.Forwarded);
        forwardedReload.State.CanaryEffectFault.ArmIntent.SourceOperationKey.Should()
            .BeEquivalentTo(sourceOperationKey);
        forwardedReload.State.CanaryEffectFault.Directive.Should().BeEquivalentTo(
            forwarded.Tool.CanaryEffectFault);
        CanaryTargetToolStep(forwardedReload.State).RetryToolInput.OperationAdmission.Should()
            .BeEquivalentTo(admission);

        var eventsBeforeAmbiguousResult = (await eventStore.GetEventsAsync(actorId)).Count;
        await forwardedReload.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatOperationResultSignal
            {
                Key = forwarded.Key.Clone(),
                Failure = new NyxIdChatOperationFailure
                {
                    FailureCode = NyxIdChatTurnGAgent.CanaryEffectFaultCode,
                    SafeMessage =
                        "The external operation may have changed state and requires exact read-back.",
                    ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
                },
            }));

        forwardedReload.State.CanaryEffectFault.Status.Should().Be(
            NyxIdChatCanaryEffectFaultStatus.Forwarded,
            "the operation result can commit before the consumed-signal retry arrives");
        var ambiguousReconciliation = (await eventStore.GetEventsAsync(actorId))
            .Skip(eventsBeforeAmbiguousResult)
            .Where(item => item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Select(item => item.EventData.Unpack<NyxIdChatOperationReconciledEvent>())
            .Should().ContainSingle().Which;
        ambiguousReconciliation.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnGAgent.CanaryEffectFaultCode);
        ambiguousReconciliation.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);
        dispatch.OperationCalls
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>())
            .Count(command => command.InputCase ==
                              NyxIdChatOperationDispatchCommand.InputOneofCase.Tool)
            .Should().Be(1, "ambiguous reconciliation must not dispatch the effect Tool again");

        var consumedSignal = new NyxIdChatCanaryEffectFaultConsumedSignal
        {
            ArmId = "arm-alpha",
            Key = forwarded.Key.Clone(),
            TurnActorId = NyxIdChatTurnActorIds.ForTurn(actorId, forwarded.Key.TurnId),
            ConsumedAt = Timestamp.FromDateTimeOffset(CanaryNow.AddMinutes(-5)),
            ServiceInstanceId = admission.ServiceInstanceId,
            ApprovalRequestId = "approval-7001-alpha",
            ReceiptStatus = AgentToolReceiptStatus.Denied,
            ApprovalDecisionMode = NyxIdApprovalDecisionMode.Unspecified,
            ApprovalTerminalOutcome = NyxIdApprovalTerminalOutcome.Rejected,
            ApprovalSubjectKind = "nyxid.user-service",
            ApprovalSubjectId = admission.ServiceInstanceId,
            ApprovalCallId = forwarded.Tool.CallId,
            ApprovalToolName = forwarded.Tool.ToolName,
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
        var consumedStep = CanaryTargetToolStep(forwardedReload.State);
        consumedStep.Source.Tool.OperationAdmission.Should().BeEquivalentTo(admission);
        consumedStep.RetryToolInput.Should().NotBeNull();
        consumedStep.ApprovalRequestId.Should().Be("approval-7001-alpha");
        consumedStep.ApprovalObservation.Should().NotBeNull();
        consumedStep.ApprovalObservation.ApprovalRequestId.Should().Be("approval-7001-alpha");
        consumedStep.ApprovalObservation.ReceiptStatus.Should().Be(AgentToolReceiptStatus.Denied);
        consumedStep.ApprovalObservation.DecisionMode.Should().Be(
            NyxIdApprovalDecisionMode.Unspecified);
        consumedStep.ApprovalObservation.TerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        consumedStep.ApprovalObservation.SubjectKind.Should().Be("nyxid.user-service");
        consumedStep.ApprovalObservation.SubjectId.Should().Be(admission.ServiceInstanceId);
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
        consumedReload.State.CanaryEffectFault.Directive.Key.Should().BeEquivalentTo(
            forwarded.Key);
        consumedReload.State.CanaryEffectFault.ArmIntent.SourceOperationKey.Should()
            .BeEquivalentTo(sourceOperationKey);
        consumedReload.State.CanaryEffectFault.ApprovalRequestId.Should().Be(
            "approval-7001-alpha");
        var reloadedStep = CanaryTargetToolStep(consumedReload.State);
        reloadedStep.Source.Tool.OperationAdmission.Should().BeEquivalentTo(admission);
        reloadedStep.RetryToolInput.Should().NotBeNull();
        reloadedStep.ApprovalRequestId.Should().Be("approval-7001-alpha");
        reloadedStep.ApprovalObservation.TerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        dispatch.OperationCalls
            .Select(call => call.Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>())
            .Count(command => command.InputCase ==
                              NyxIdChatOperationDispatchCommand.InputOneofCase.Tool)
            .Should().Be(1,
                "the delayed acknowledgement is an idempotent state transition, not a redispatch");
    }

    private static NyxIdChatConversationGAgentState CanaryConversationState()
    {
        var now = Timestamp.FromDateTimeOffset(CanaryNow.AddMinutes(-1));
        var sourceOperationKey = CanarySourceLlmOperationKey();
        var sourceStep = new NyxIdChatTaskStepState
        {
            StepId = sourceOperationKey.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = sourceOperationKey.Clone(),
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
                RequestedAt = now.Clone(),
                DispatchedAt = now.Clone(),
            },
            Source = new NyxIdChatStepSource
            {
                Llm = new NyxIdChatLLMStepSource { Model = "model-alpha" },
            },
            AvailableActions = new NyxIdChatAvailableActions { Stop = true },
            AddedInPlanRevision = 1,
            UpdatedAt = now.Clone(),
        };
        var task = new NyxIdChatTaskState
        {
            TaskId = sourceOperationKey.TaskId,
            TurnId = sourceOperationKey.TurnId,
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = sourceOperationKey.StepId,
            ActiveOperationId = sourceOperationKey.OperationId,
            PlanId = "plan-alpha",
            PlanRevision = 1,
            PlanRevisionHistoryStart = 1,
            CreatedAt = now.Clone(),
            UpdatedAt = now.Clone(),
        };
        task.Steps.Add(sourceStep);
        task.PlanRevisions.Add(new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = 1,
            RevisionCause = NyxIdChatPlanRevisionCause.Initial,
            CommittedAt = now.Clone(),
            AddedStepIds = { sourceOperationKey.StepId },
        });
        var turn = new NyxIdChatTurnState
        {
            TurnId = sourceOperationKey.TurnId,
            TaskId = sourceOperationKey.TaskId,
            Status = NyxIdChatTurnStatus.Active,
            CreatedAt = now.Clone(),
        };
        return new NyxIdChatConversationGAgentState
        {
            ConversationActorId = sourceOperationKey.ConversationActorId,
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            ProgressSequence = 4,
            UpdatedAt = now.Clone(),
        };
    }

    private static NyxIdChatTaskStepState CanaryTargetToolStep(
        NyxIdChatConversationGAgentState state) =>
        state.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool &&
            step.Operation?.Key?.Equals(state.CanaryEffectFault.Directive.Key) == true);

    private static NyxIdChatCanaryEffectFaultArmCommand CanaryArmCommand(
        long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        ArmId = "arm-alpha",
        ClientRequestId = "client-arm-alpha",
        SourceOperationKey = CanarySourceLlmOperationKey(),
        ServiceInstanceId = "connected-service-alpha",
        OwnerSubject = "owner-alpha",
        ExpiresAt = Timestamp.FromDateTimeOffset(CanaryNow.AddMinutes(5)),
        ExpectedStateVersion = expectedStateVersion,
        CommandId = "command-arm-alpha",
        CorrelationId = "correlation-arm-alpha",
    };

    private static NyxIdChatOperationKey CanarySourceLlmOperationKey() => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-llm-alpha",
        OperationId = "operation-llm-alpha",
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
                        AgentToolOperationExecutionModePayload.Durable,
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
