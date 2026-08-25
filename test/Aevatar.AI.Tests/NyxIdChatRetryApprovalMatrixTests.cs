using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatConversationGAgentTests
{
    [Fact]
    public async Task UC3UnprofiledReconcileRetry_WithValidGrant_ShouldCommitGenerationTwoAndVerifySuccess()
    {
        const string actorId = "conversation-uc3-grant";
        var eventStore = new InMemoryEventStoreForTests();
        var initialState = CreateRetryMatrixState(actorId);
        initialState.AgentProfile = null;
        initialState.ActiveTurn.AgentProfileTurnAuthority = null;
        initialState.LatestTurn.AgentProfileTurnAuthority = null;
        await PersistTestStateAsync(eventStore, actorId, 1, initialState);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId, dispatch);
        await agent.ActivateAsync();
        var effect = MatrixEffectStep(agent.State);

        await agent.HandleEventAsync(CreateEnvelope(actorId, MatrixUncertain(effect.Operation.Key)));

        effect = MatrixEffectStep(agent.State);
        effect.Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        effect.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        effect.AvailableActions.Retry.Should().BeFalse();
        var reconciliationDispatch = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope
            .Payload.Unpack<NyxIdChatOperationDispatchCommand>();
        reconciliationDispatch.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification);
        agent.State.ActiveTask.PlanRevisions[^1].RevisionCause.Should().Be(
            NyxIdChatPlanRevisionCause.FailureRecovery);

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixVerification(
                reconciliationDispatch.Key,
                effect.StepId,
                NyxIdChatToolVerificationDisposition.NotApplied)));

        effect = MatrixEffectStep(agent.State);
        effect.Status.Should().Be(NyxIdChatStepStatus.Failed);
        effect.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        effect.AvailableActions.Retry.Should().BeTrue();
        var beforeRetry = await eventStore.GetEventsAsync(actorId);
        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixRetryCommand(actorId, effect, beforeRetry[^1].Version)));

        dispatch.OperationCalls.Should().HaveCount(2,
            "the explicit retry dispatches generation two immediately");
        agent.State.ActiveTask.PlanRevision.Should().Be(3);
        var retryDispatch = dispatch.OperationCalls[^1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        retryDispatch.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        retryDispatch.Key.OperationGeneration.Should().Be(2);
        retryDispatch.Tool.OperationAdmission.ServiceInstanceId.Should().Be("svc-lark");
        retryDispatch.Tool.RematerializeDurableAuthorization.Should().BeTrue();
        retryDispatch.Tool.RetryAuthorizationSourceKey.Should().BeEquivalentTo(
            reconciliationDispatch.Key);
        retryDispatch.Tool.AgentProfile.Should().BeNull();
        retryDispatch.Tool.AgentProfileTurnAuthority.Should().BeNull();
        retryDispatch.Tool.ToolContext.Credentials.NyxIdAccessToken.Should().Be(
            "retry-capability-alpha");
        agent.State.ToString().Should().NotContain("retry-capability-alpha");

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixToolReceipt(
                retryDispatch.Key,
                AgentToolReceiptStatus.Success,
                NyxIdChatEffectEvidence.Confirmed,
                "",
                NyxIdApprovalDecisionMode.Grant,
                "")));

        agent.State.PendingApproval.Should().BeNull();
        MatrixEffectStep(agent.State).ApprovalRequestId.Should().BeEmpty(
            "a still-valid NyxID grant must not create an Aevatar approval identity");
        dispatch.OperationCalls.Should().HaveCount(3);
        var successVerification = dispatch.OperationCalls[^1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        successVerification.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification);

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixVerification(
                successVerification.Key,
                effect.StepId,
                NyxIdChatToolVerificationDisposition.Applied)));

        MatrixEffectStep(agent.State).ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.Confirmed);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        dispatch.OperationCalls.Should().HaveCount(3,
            "the effect runs only for the explicit generation-two retry");
        var events = await eventStore.GetEventsAsync(actorId);
        events.Count(item => item.EventData.Is(NyxIdChatStepControlCommittedEvent.Descriptor))
            .Should().Be(1);
        events.Count(item => item.EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor))
            .Should().Be(3);
    }

    [Fact]
    public async Task UC3Retry_PerRequestApproval_ShouldPersistFreshRequestWithoutSyntheticPendingApproval()
    {
        const string actorId = "conversation-uc3-per-request";
        var eventStore = new InMemoryEventStoreForTests();
        var state = CreateRetryMatrixState(actorId);
        var effect = MatrixEffectStep(state);
        effect.Status = NyxIdChatStepStatus.Failed;
        effect.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        effect.Operation.Phase = NyxIdChatOperationPhase.Failed;
        effect.Operation.CompletedAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        effect.ApprovalRequestId = "approval-generation-one";
        effect.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(effect);
        state.ActiveTask.ActiveStepId = string.Empty;
        state.ActiveTask.ActiveOperationId = string.Empty;
        state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Postcondition)
            .Status = NyxIdChatStepStatus.Cancelled;
        await PersistTestStateAsync(eventStore, actorId, 1, state);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId, dispatch);
        await agent.ActivateAsync();
        var beforeRetry = await eventStore.GetEventsAsync(actorId);

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixRetryCommand(actorId, MatrixEffectStep(agent.State), beforeRetry[^1].Version)));

        var retry = dispatch.OperationCalls.Should().ContainSingle(
                "the explicit retry dispatches generation two immediately")
            .Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        retry.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        retry.Key.OperationGeneration.Should().Be(2);
        retry.Tool.RematerializeDurableAuthorization.Should().BeTrue();
        retry.Tool.RetryAuthorizationSourceKey.Should().BeEquivalentTo(effect.Operation.Key);
        MatrixEffectStep(agent.State).ApprovalRequestId.Should().BeEmpty();

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixToolReceipt(
                retry.Key,
                AgentToolReceiptStatus.ApprovalRequired,
                NyxIdChatEffectEvidence.NotStarted,
                "NYXID_APPROVAL_REQUIRED",
                NyxIdApprovalDecisionMode.PerRequest,
                "approval-generation-two")));

        var observed = MatrixEffectStep(agent.State);
        observed.Operation.Key.OperationGeneration.Should().Be(2);
        observed.ApprovalRequestId.Should().Be("approval-generation-two");
        observed.ApprovalRequestId.Should().NotBe("approval-generation-one");
        observed.ApprovalObservation.Should().NotBeNull();
        observed.ApprovalObservation.ApprovalRequestId.Should().Be("approval-generation-two");
        observed.ApprovalObservation.ReceiptStatus.Should().Be(
            AgentToolReceiptStatus.ApprovalRequired);
        observed.ApprovalObservation.DecisionMode.Should().Be(
            NyxIdApprovalDecisionMode.PerRequest);
        observed.Status.Should().Be(NyxIdChatStepStatus.Failed);
        observed.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        observed.AvailableActions.Retry.Should().BeTrue();
        agent.State.PendingApproval.Should().BeNull(
            "the real request is decided only on a NyxID-owned surface");
        dispatch.OperationCalls.Should().ContainSingle(
            "post-return observation cannot auto-replay the effect");
    }

    [Theory]
    [InlineData("per-request-approved", NyxIdApprovalDecisionMode.PerRequest,
        AgentToolReceiptStatus.Success, NyxIdChatEffectEvidence.Confirmed,
        "", NyxIdChatStepStatus.Waiting, NyxIdChatEffectEvidence.MayHaveChanged,
        false, true, true)]
    [InlineData("grant-approved", NyxIdApprovalDecisionMode.Grant,
        AgentToolReceiptStatus.Success, NyxIdChatEffectEvidence.Confirmed,
        "", NyxIdChatStepStatus.Waiting, NyxIdChatEffectEvidence.MayHaveChanged,
        false, true, true)]
    [InlineData("per-request-denied", NyxIdApprovalDecisionMode.PerRequest,
        AgentToolReceiptStatus.Denied, NyxIdChatEffectEvidence.NotApplied,
        "NYXID_APPROVAL_FAILED", NyxIdChatStepStatus.Cancelled,
        NyxIdChatEffectEvidence.NotApplied, true, false, true)]
    [InlineData("grant-denied", NyxIdApprovalDecisionMode.Grant,
        AgentToolReceiptStatus.Denied, NyxIdChatEffectEvidence.NotApplied,
        "NYXID_APPROVAL_FAILED", NyxIdChatStepStatus.Cancelled,
        NyxIdChatEffectEvidence.NotApplied, true, false, true)]
    public async Task TierBPublishedOutcome_ShouldCommitActorFactWithoutAutomaticEffectReplay(
        string scenario,
        NyxIdApprovalDecisionMode decisionMode,
        AgentToolReceiptStatus status,
        NyxIdChatEffectEvidence reportedEffect,
        string errorCode,
        NyxIdChatStepStatus expectedStatus,
        NyxIdChatEffectEvidence expectedEffect,
        bool expectedRetry,
        bool expectsVerification,
        bool retainsApprovalIdentity)
    {
        var actorId = $"conversation-tier-b-{scenario}";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, actorId, 1, CreateRetryMatrixState(actorId));
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId, dispatch);
        await agent.ActivateAsync();
        var key = MatrixEffectStep(agent.State).Operation.Key.Clone();

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixToolReceipt(
                key,
                status,
                reportedEffect,
                errorCode,
                decisionMode,
                $"approval-{scenario}")));

        var step = MatrixEffectStep(agent.State);
        step.Status.Should().Be(expectedStatus);
        step.ExternalEffect.Should().Be(expectedEffect);
        step.ApprovalRequestId.Should().Be(
            retainsApprovalIdentity ? $"approval-{scenario}" : string.Empty,
            "uncertain tool receipts normalize to failure evidence before persistence");
        step.AvailableActions.Retry.Should().Be(expectedRetry,
            "only typed not_applied evidence may unlock explicit retry");
        agent.State.PendingApproval.Should().BeNull();
        dispatch.OperationCalls.Should().HaveCount(expectsVerification ? 1 : 0);
        dispatch.OperationCalls.Should().OnlyContain(call =>
            call.Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>().InputCase ==
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification);
    }

    [Theory]
    [InlineData("Approval request was rejected", "per_request", NyxIdApprovalDecisionMode.PerRequest)]
    [InlineData("Approval request expired", "per_request", NyxIdApprovalDecisionMode.PerRequest)]
    [InlineData("Approval request timed out", "per_request", NyxIdApprovalDecisionMode.PerRequest)]
    [InlineData("Approval request was rejected", "grant", NyxIdApprovalDecisionMode.Grant)]
    [InlineData("Approval request expired", "grant", NyxIdApprovalDecisionMode.Grant)]
    [InlineData("Approval request timed out", "grant", NyxIdApprovalDecisionMode.Grant)]
    public async Task Published7001ApprovalFailure_ShouldCommitSameHonestDeniedFact(
        string providerReason,
        string approvalMode,
        NyxIdApprovalDecisionMode expectedDecisionMode)
    {
        const string actorId = "conversation-tier-b-7001";
        var receipt = NyxIdProxyReceiptFactory.TryCreate(
            "call-effect",
            "lark-create-approval",
            "lark",
            "svc-lark",
            "Lark",
            resourceUri: null,
            $$"""{"error":"approval_failed","error_code":7001,"message":"Approval failed: {{providerReason}}. Review pending approvals on NyxID","request_id":"approval-7001","approval_mode":"{{approvalMode}}"}""")!;
        receipt.Status.Should().Be(AgentToolReceiptStatus.Denied);
        receipt.ErrorCode.Should().Be("NYXID_APPROVAL_FAILED");
        receipt.ApprovalRequestId.Should().Be("approval-7001");
        receipt.NyxIdApprovalDecisionMode.Should().Be(expectedDecisionMode);
        receipt.Effect = AgentToolReceiptEffect.Mutating;
        receipt.SideEffectKind = "lark.approval.create";

        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, actorId, 1, CreateRetryMatrixState(actorId));
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId, dispatch);
        await agent.ActivateAsync();
        var key = MatrixEffectStep(agent.State).Operation.Key.Clone();

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatOperationResultSignal
            {
                Key = key,
                Tool = new NyxIdChatToolOperationResult
                {
                    ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                    Receipt = receipt,
                },
            }));

        var step = MatrixEffectStep(agent.State);
        step.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        step.ApprovalRequestId.Should().Be("approval-7001");
        step.ApprovalObservation.Should().NotBeNull();
        step.ApprovalObservation.ReceiptStatus.Should().Be(AgentToolReceiptStatus.Denied);
        step.ApprovalObservation.DecisionMode.Should().Be(expectedDecisionMode);
        step.AvailableActions.Retry.Should().BeTrue();
        dispatch.OperationCalls.Should().BeEmpty(
            "NyxID publishes one 7001 contract for rejected, expired, and timed-out decisions");
    }

    [Fact]
    public async Task Raw7000ApprovalRequired_ShouldCommitExactPostReturnActorObservation()
    {
        const string actorId = "conversation-tier-b-7000";
        var receipt = NyxIdProxyReceiptFactory.TryCreate(
            "call-effect",
            "lark-create-approval",
            "lark",
            "svc-lark",
            "Lark",
            resourceUri: null,
            """{"error":"approval_required","error_code":7000,"message":"Approval required","request_id":"approval-7000","approval_mode":"grant"}""")!;
        receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        receipt.ApprovalRequestId.Should().Be("approval-7000");
        receipt.NyxIdApprovalDecisionMode.Should().Be(NyxIdApprovalDecisionMode.Grant);
        receipt.Effect = AgentToolReceiptEffect.Mutating;
        receipt.SideEffectKind = "lark.approval.create";

        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, actorId, 1, CreateRetryMatrixState(actorId));
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId, dispatch);
        await agent.ActivateAsync();
        var key = MatrixEffectStep(agent.State).Operation.Key.Clone();

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatOperationResultSignal
            {
                Key = key,
                Tool = new NyxIdChatToolOperationResult
                {
                    ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                    Receipt = receipt,
                },
            }));

        var step = MatrixEffectStep(agent.State);
        step.Status.Should().Be(NyxIdChatStepStatus.Failed);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        step.ApprovalRequestId.Should().Be("approval-7000");
        step.ApprovalObservation.Should().NotBeNull();
        step.ApprovalObservation.DecisionMode.Should().Be(NyxIdApprovalDecisionMode.Grant);
        step.ApprovalObservation.ReceiptStatus.Should().Be(
            AgentToolReceiptStatus.ApprovalRequired);
        agent.State.PendingApproval.Should().BeNull(
            "Tier B learns the approval identity only after NyxID returns");
        dispatch.OperationCalls.Should().BeEmpty();

        var committed = await eventStore.GetEventsAsync(actorId);
        var observation = committed.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Which.EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        observation.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-7000");
        observation.State.ActiveTask.Steps.Single(candidate => candidate.StepId == step.StepId)
            .ApprovalObservation.ApprovalRequestId.Should().Be("approval-7000");
    }

    [Fact]
    public async Task RestartWithCommittedEffectDispatch_ShouldSignalReconciliationWithoutToolReplay()
    {
        const string actorId = "conversation-tier-b-restart";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, actorId, 1, CreateRetryMatrixState(actorId));
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, callbackScheduler: callbacks);
        var recovered = CreateController(services, actorId, dispatch);

        await recovered.ActivateAsync();

        dispatch.OperationCalls.Should().BeEmpty();
        var recovery = callbacks.TimeoutRequests.Should().ContainSingle(request =>
                request.TriggerEnvelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor))
            .Which.TriggerEnvelope.Clone();
        var signal = recovery.Payload.Unpack<NyxIdChatRecoveryRequestedSignal>();
        signal.Kind.Should().Be(NyxIdChatRecoveryKind.InterruptedOperationReconciliation);
        signal.Key.Should().BeEquivalentTo(MatrixEffectStep(recovered.State).Operation.Key);

        dispatch.OperationCalls.Should().BeEmpty(
            "activation may only schedule an actor-inbox reconciliation signal");
        MatrixEffectStep(recovered.State).Operation.Key.OperationGeneration.Should().Be(1);
        MatrixEffectStep(recovered.State).ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted,
            "activation queues recovery but does not mutate the committed effect fact");
        MatrixEffectStep(recovered.State).AvailableActions.Retry.Should().BeFalse();
    }

    [Theory]
    [InlineData(
        "approve-during-restart",
        AgentToolReceiptStatus.Success,
        NyxIdChatEffectEvidence.Confirmed,
        "",
        NyxIdApprovalDecisionMode.Grant,
        "approval-approved",
        NyxIdChatToolVerificationDisposition.Applied,
        NyxIdChatStepStatus.Done,
        NyxIdChatEffectEvidence.Confirmed,
        false)]
    [InlineData(
        "per-request-approve-during-restart",
        AgentToolReceiptStatus.Success,
        NyxIdChatEffectEvidence.Confirmed,
        "",
        NyxIdApprovalDecisionMode.PerRequest,
        "approval-per-request-approved",
        NyxIdChatToolVerificationDisposition.Applied,
        NyxIdChatStepStatus.Done,
        NyxIdChatEffectEvidence.Confirmed,
        false)]
    [InlineData(
        "deny-during-restart",
        AgentToolReceiptStatus.Denied,
        NyxIdChatEffectEvidence.NotApplied,
        "NYXID_APPROVAL_DENIED",
        NyxIdApprovalDecisionMode.PerRequest,
        "approval-denied",
        NyxIdChatToolVerificationDisposition.NotApplied,
        NyxIdChatStepStatus.Failed,
        NyxIdChatEffectEvidence.NotApplied,
        true)]
    [InlineData(
        "grant-deny-during-restart",
        AgentToolReceiptStatus.Denied,
        NyxIdChatEffectEvidence.NotApplied,
        "NYXID_APPROVAL_DENIED",
        NyxIdApprovalDecisionMode.Grant,
        "approval-grant-denied",
        NyxIdChatToolVerificationDisposition.NotApplied,
        NyxIdChatStepStatus.Failed,
        NyxIdChatEffectEvidence.NotApplied,
        true)]
    [InlineData(
        "effect-completed-during-restart",
        AgentToolReceiptStatus.Success,
        NyxIdChatEffectEvidence.Confirmed,
        "",
        NyxIdApprovalDecisionMode.PerRequest,
        "",
        NyxIdChatToolVerificationDisposition.Applied,
        NyxIdChatStepStatus.Done,
        NyxIdChatEffectEvidence.Confirmed,
        false)]
    [InlineData(
        "grant-effect-completed-during-restart",
        AgentToolReceiptStatus.Success,
        NyxIdChatEffectEvidence.Confirmed,
        "",
        NyxIdApprovalDecisionMode.Grant,
        "",
        NyxIdChatToolVerificationDisposition.Applied,
        NyxIdChatStepStatus.Done,
        NyxIdChatEffectEvidence.Confirmed,
        false)]
    public async Task RestartReconciliation_WithLateOldGenerationOutcome_ShouldNeverReplayEffect(
        string scenario,
        AgentToolReceiptStatus lateStatus,
        NyxIdChatEffectEvidence lateEffect,
        string lateErrorCode,
        NyxIdApprovalDecisionMode decisionMode,
        string approvalRequestId,
        NyxIdChatToolVerificationDisposition disposition,
        NyxIdChatStepStatus expectedStatus,
        NyxIdChatEffectEvidence expectedEffect,
        bool expectedRetry)
    {
        var actorId = $"conversation-tier-b-recovery-first-{scenario}";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, actorId, 1, CreateRetryMatrixState(actorId));
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, callbackScheduler: callbacks);
        var recovered = CreateController(services, actorId, dispatch);
        await recovered.ActivateAsync();
        var recovery = GetMatrixRecoveryEnvelope(callbacks);
        var oldGenerationKey = MatrixEffectStep(recovered.State).Operation.Key.Clone();

        await recovered.HandleEventAsync(recovery);

        var reconciliation = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope
            .Payload.Unpack<NyxIdChatOperationDispatchCommand>();
        reconciliation.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification);
        reconciliation.ToolVerification.EffectStepId.Should().Be(oldGenerationKey.StepId);
        reconciliation.ToolVerification.ReadBack.Should().BeEquivalentTo(
            MatrixAdmission().ReadBack);
        var uncertain = MatrixEffectStep(recovered.State);
        uncertain.Operation.Key.Should().BeEquivalentTo(oldGenerationKey);
        uncertain.Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        uncertain.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        uncertain.AvailableActions.Retry.Should().BeFalse();

        await recovered.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixToolReceipt(
                oldGenerationKey,
                lateStatus,
                lateEffect,
                lateErrorCode,
                decisionMode,
                approvalRequestId)));

        dispatch.OperationCalls.Should().ContainSingle(
            "a late old-generation decision or completion cannot replay the effect");
        var stillUncertain = MatrixEffectStep(recovered.State);
        stillUncertain.Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        stillUncertain.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        stillUncertain.AvailableActions.Retry.Should().BeFalse();

        await recovered.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixVerification(reconciliation.Key, oldGenerationKey.StepId, disposition)));

        var reconciled = MatrixEffectStep(recovered.State);
        reconciled.Operation.Key.Should().BeEquivalentTo(oldGenerationKey);
        reconciled.Status.Should().Be(expectedStatus);
        reconciled.ExternalEffect.Should().Be(expectedEffect);
        reconciled.AvailableActions.Retry.Should().Be(expectedRetry);
        dispatch.OperationCalls.Should().ContainSingle();
        dispatch.OperationCalls.Should().OnlyContain(call =>
            call.Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>().InputCase ==
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification);
    }

    [Theory]
    [InlineData(
        "approve-before-recovery",
        AgentToolReceiptStatus.Success,
        NyxIdChatEffectEvidence.Confirmed,
        "",
        NyxIdApprovalDecisionMode.Grant,
        "approval-approved",
        1,
        NyxIdChatStepStatus.Waiting,
        NyxIdChatEffectEvidence.MayHaveChanged,
        false)]
    [InlineData(
        "deny-before-recovery",
        AgentToolReceiptStatus.Denied,
        NyxIdChatEffectEvidence.NotApplied,
        "NYXID_APPROVAL_DENIED",
        NyxIdApprovalDecisionMode.PerRequest,
        "approval-denied",
        0,
        NyxIdChatStepStatus.Cancelled,
        NyxIdChatEffectEvidence.NotApplied,
        true)]
    [InlineData(
        "effect-completed-before-recovery",
        AgentToolReceiptStatus.Success,
        NyxIdChatEffectEvidence.Confirmed,
        "",
        NyxIdApprovalDecisionMode.PerRequest,
        "",
        1,
        NyxIdChatStepStatus.Waiting,
        NyxIdChatEffectEvidence.MayHaveChanged,
        false)]
    public async Task RestartOutcomeBeforeRecoverySignal_ShouldFenceStaleRecoveryWithoutReplay(
        string scenario,
        AgentToolReceiptStatus status,
        NyxIdChatEffectEvidence reportedEffect,
        string errorCode,
        NyxIdApprovalDecisionMode decisionMode,
        string approvalRequestId,
        int expectedVerificationDispatches,
        NyxIdChatStepStatus expectedStatus,
        NyxIdChatEffectEvidence expectedEffect,
        bool expectedRetry)
    {
        var actorId = $"conversation-tier-b-result-first-{scenario}";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, actorId, 1, CreateRetryMatrixState(actorId));
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, callbackScheduler: callbacks);
        var recovered = CreateController(services, actorId, dispatch);
        await recovered.ActivateAsync();
        var recovery = GetMatrixRecoveryEnvelope(callbacks);
        var key = MatrixEffectStep(recovered.State).Operation.Key.Clone();

        await recovered.HandleEventAsync(CreateEnvelope(
            actorId,
            MatrixToolReceipt(
                key,
                status,
                reportedEffect,
                errorCode,
                decisionMode,
                approvalRequestId)));
        await recovered.HandleEventAsync(recovery);

        var observed = MatrixEffectStep(recovered.State);
        observed.Status.Should().Be(expectedStatus);
        observed.ExternalEffect.Should().Be(expectedEffect);
        observed.AvailableActions.Retry.Should().Be(expectedRetry);
        dispatch.OperationCalls.Should().HaveCount(expectedVerificationDispatches,
            "the stale activation callback cannot create a second dispatch");
        dispatch.OperationCalls.Should().OnlyContain(call =>
            call.Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>().InputCase ==
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification);
    }

    private static EventEnvelope GetMatrixRecoveryEnvelope(
        RecordingRuntimeCallbackScheduler callbacks) =>
        callbacks.TimeoutRequests.Should().ContainSingle(request =>
                request.TriggerEnvelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor))
            .Which.TriggerEnvelope.Clone();

    private static NyxIdChatConversationGAgentState CreateRetryMatrixState(string actorId)
    {
        var now = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 8, 8, 0, 0, TimeSpan.Zero));
        var admission = MatrixAdmission();
        var effectKey = MatrixKey(actorId, "step-effect", "operation-effect", 1);
        var verificationKey = MatrixKey(actorId, "step-verification", "operation-verification", 1);
        var effect = new NyxIdChatTaskStepState
        {
            StepId = effectKey.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = "Create the sanctioned Lark approval instance.",
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = "lark-create-approval",
                    ServiceSlug = "lark",
                    ServiceId = "svc-lark",
                    OperationAdmission = admission.Clone(),
                },
            },
            MayChangeExternalState = true,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            RetryInputRebuildable = true,
            RetryToolInput = new NyxIdChatRetryToolInputState
            {
                CallId = "call-effect",
                ToolName = "lark-create-approval",
                Arguments = JsonParser.Default.Parse<Struct>("{\"approvalCode\":\"canary\"}"),
                OperationAdmission = admission.Clone(),
            },
            Operation = new NyxIdChatOperationState
            {
                Key = effectKey,
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Dispatched,
                MayChangeExternalState = true,
                Idempotent = false,
                IdempotencyKey = "effect-idempotency-alpha",
                RequestedAt = now.Clone(),
                DispatchedAt = now.Clone(),
            },
            AddedBy = NyxIdChatStepAddedBy.Initial,
            UpdatedAt = now.Clone(),
        };
        effect.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(effect);
        var verification = new NyxIdChatTaskStepState
        {
            StepId = verificationKey.StepId,
            Order = 2,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Description = "Verify the Lark approval instance.",
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    EffectStepId = effect.StepId,
                    Check = admission.ReadBack.CheckName,
                    ToolReadBack = admission.ReadBack.Clone(),
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = verificationKey,
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            AddedBy = NyxIdChatStepAddedBy.Initial,
            DependsOn = { effect.StepId },
            UpdatedAt = now.Clone(),
        };
        verification.AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(verification);
        var task = new NyxIdChatTaskState
        {
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = effect.StepId,
            ActiveOperationId = effect.Operation.Key.OperationId,
            SchemaVersion = 4,
            ActorId = actorId,
            PlanId = "plan-alpha",
            PlanRevision = 1,
            CreatedAt = now.Clone(),
            UpdatedAt = now.Clone(),
        };
        task.Steps.Add(effect);
        task.Steps.Add(verification);
        task.PlanRevisions.Add(new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = 1,
            RevisionCause = NyxIdChatPlanRevisionCause.Initial,
            CommittedAt = now.Clone(),
            AddedStepIds = { effect.StepId, verification.StepId },
        });
        var turn = new NyxIdChatTurnState
        {
            TurnId = task.TurnId,
            TaskId = task.TaskId,
            Status = NyxIdChatTurnStatus.Active,
            AgentProfileTurnAuthority = new AgentProfileTurnAuthorityState(),
        };
        return new NyxIdChatConversationGAgentState
        {
            ConversationActorId = actorId,
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            AgentProfile = new AgentProfileSnapshot(),
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            HistoryDeliveryReservation = new NyxIdChatHistoryDeliveryReservationState
            {
                DeliveryId = "delivery-alpha",
                TurnId = turn.TurnId,
                SourceActorId = actorId,
                SourceCommandId = "command-alpha",
                Dispatched = true,
                DispatchedAt = now.Clone(),
            },
            ProgressSequence = 1,
            UpdatedAt = now,
        };
    }

    private static NyxIdChatConversationGAgentState CreateFailedRetryMatrixState(string actorId)
    {
        var state = CreateRetryMatrixState(actorId);
        var effect = MatrixEffectStep(state);
        effect.Status = NyxIdChatStepStatus.Failed;
        effect.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
        effect.Operation.Phase = NyxIdChatOperationPhase.Failed;
        effect.Operation.CompletedAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        effect.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(effect);
        state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Postcondition)
            .Status = NyxIdChatStepStatus.Cancelled;
        state.ActiveTask.ActiveStepId = string.Empty;
        state.ActiveTask.ActiveOperationId = string.Empty;
        return state;
    }

    private static NyxIdChatOperationKey MatrixKey(
        string actorId,
        string stepId,
        string operationId,
        long generation) => new()
    {
        ConversationActorId = actorId,
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = stepId,
        OperationId = operationId,
        OperationGeneration = generation,
    };

    private static AgentToolOperationAdmissionPayload MatrixAdmission() => new()
    {
        ServiceInstanceId = "svc-lark",
        ServiceSlug = "lark",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "approval.create",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "POST",
        PathTemplate = "/approvals",
        ContractDigest = new string('b', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.Write,
            Approval = AgentToolOperationApprovalPayload.Required,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes = { AgentToolOperationExecutionModePayload.Interactive },
        },
        ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = new AgentToolOperationAdmissionPayload
            {
                ServiceInstanceId = "svc-lark",
                ServiceSlug = "lark",
                PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
                {
                    EndpointId = "approval.list",
                },
                AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
                HttpMethod = "GET",
                PathTemplate = "/approvals",
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
            },
            Arguments = JsonParser.Default.Parse<Struct>("{}"),
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Exists,
                JsonPointer = "/data/items/0",
            },
            CheckName = "approval_instance_exists",
        },
    };

    private static NyxIdChatTaskStepState MatrixEffectStep(
        NyxIdChatConversationGAgentState state) =>
        state.ActiveTask.Steps.Single(step => step.StepId == "step-effect");

    private static NyxIdChatOperationResultSignal MatrixUncertain(NyxIdChatOperationKey key) =>
        new()
        {
            Key = key.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "TOOL_OUTCOME_UNKNOWN",
                SafeMessage = "The external outcome could not be confirmed.",
                ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
            },
        };

    private static NyxIdChatOperationResultSignal MatrixVerification(
        NyxIdChatOperationKey key,
        string effectStepId,
        NyxIdChatToolVerificationDisposition disposition) => new()
    {
        Key = key.Clone(),
        ToolVerification = new NyxIdChatToolVerificationResult
        {
            EffectStepId = effectStepId,
            Disposition = disposition,
            ReadOperation = MatrixAdmission().ReadBack.ReadOperation.Clone(),
            CheckName = MatrixAdmission().ReadBack.CheckName,
            FailureCode = disposition == NyxIdChatToolVerificationDisposition.NotApplied
                ? "EFFECT_NOT_FOUND"
                : string.Empty,
            SafeMessage = disposition == NyxIdChatToolVerificationDisposition.NotApplied
                ? "The read-back proved that the effect was not applied."
                : string.Empty,
        },
    };

    private static NyxIdChatRetryStepCommand MatrixRetryCommand(
        string actorId,
        NyxIdChatTaskStepState step,
        long stateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = actorId,
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = step.StepId,
        RetryRequestId = "retry-effect-alpha",
        ClientRequestId = "client-retry-effect-alpha",
        CommandId = "command-retry-effect-alpha",
        CorrelationId = "correlation-retry-effect-alpha",
        OwnerSubject = "owner-alpha",
        ExpectedOperationGeneration = step.Operation.Key.OperationGeneration,
        ExpectedStateVersion = stateVersion,
        ToolContext = (AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("retry-effect-alpha", null),
            Credentials = new AgentToolCredentials(
                "retry-capability-alpha",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "owner-alpha",
                "retry-effect-alpha",
                "scope-alpha"),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "owner-alpha",
                "proxy"),
            ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
        }).ToPayload(),
    };

    private static NyxIdChatOperationResultSignal MatrixToolReceipt(
        NyxIdChatOperationKey key,
        AgentToolReceiptStatus status,
        NyxIdChatEffectEvidence externalEffect,
        string errorCode,
        NyxIdApprovalDecisionMode decisionMode,
        string approvalRequestId) => new()
    {
        Key = key.Clone(),
        Tool = new NyxIdChatToolOperationResult
        {
            ExternalEffect = externalEffect,
            Receipt = new AgentToolReceipt
            {
                CallId = "call-effect",
                ToolName = "lark-create-approval",
                Status = status,
                Effect = AgentToolReceiptEffect.Mutating,
                SideEffectKind = "lark.approval.create",
                ErrorCode = errorCode,
                ErrorMessage = string.IsNullOrWhiteSpace(errorCode)
                    ? string.Empty
                    : "NyxID returned a typed approval outcome.",
                ApprovalRequestId = approvalRequestId,
                NyxIdApprovalDecisionMode = decisionMode,
            },
        },
    };
}

public sealed partial class NyxIdChatTurnGAgentTests
{
    [Fact]
    public async Task ExactConnectedService_GenericToolApprovalSentinel_ShouldFailInsideTurnActor()
    {
        var admission = SentinelApprovalAdmission();
        var generation = new SentinelApprovalReplyExecutor(admission);
        var executor = new NyxIdChatTurnOperationExecutor(generation);
        var operationDispatch = new RecordingOperationDispatchPort(executor);
        var conversationDispatch = new RecordingDispatchPort();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(services, operationDispatch, conversationDispatch);
        await agent.ActivateAsync();
        var initial = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = new ChatRequestEvent
                {
                    Prompt = "Create the sanctioned Lark approval.",
                    SessionId = "turn-alpha",
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", initial));
        await operationDispatch.DeliverPendingSignalsAsync(agent);
        var generated = conversationDispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor))
            .Which.Envelope.Payload.Unpack<NyxIdChatOperationResultSignal>();
        var sealedAdmission = generated.Llm.ToolCalls.Should().ContainSingle().Which
            .OperationAdmission;
        conversationDispatch.Calls.Clear();
        var tool = new NyxIdChatOperationDispatchCommand
        {
            Key = CreateKey("step-lark", "operation-lark", 1),
            Tool = new NyxIdChatToolOperationInput
            {
                ToolName = SentinelApprovalReplyExecutor.ToolCall.Name,
                CallId = SentinelApprovalReplyExecutor.ToolCall.Id,
                ArgumentsJson = SentinelApprovalReplyExecutor.ToolCall.ArgumentsJson,
                MayChangeExternalState = true,
                Idempotent = false,
                IdempotencyKey = "operation-lark",
                OperationAdmission = sealedAdmission.Clone(),
            },
        };

        await agent.HandleEventAsync(CreateEnvelope("turn-actor-alpha", tool));
        await operationDispatch.DeliverPendingSignalsAsync(agent);

        generation.ToolExecutions.Should().Be(1);
        var delivered = conversationDispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(NyxIdChatOperationResultSignal.Descriptor))
            .Which.Envelope.Payload.Unpack<NyxIdChatOperationResultSignal>();
        delivered.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        delivered.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolApprovalRequestIdRequiredCode);
        delivered.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        delivered.ToString().Should().NotContain("svc-lark");
        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Failed);
    }

    private static AgentToolOperationAdmissionPayload SentinelApprovalAdmission()
    {
        var admission = ExactWriteAdmission();
        admission.ServiceInstanceId = "svc-lark";
        admission.ServiceSlug = "lark";
        admission.PublishedEndpoint.EndpointId = "approval.create";
        admission.HttpMethod = "POST";
        admission.PathTemplate = "/approvals";
        return admission;
    }

    private sealed class SentinelApprovalReplyExecutor(
        AgentToolOperationAdmissionPayload admission)
        : IAgentRunReplyGenerationExecutorPort
    {
        public static AgentRunToolCall ToolCall { get; } = new()
        {
            Id = "call-lark",
            Name = "lark-create-approval",
            ArgumentsJson = "{\"approvalCode\":\"canary\"}",
        };

        public int ToolExecutions { get; private set; }

        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentRunReplyStepState
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                NextStepIndex = 1,
                MaxToolRounds = 2,
            });
        }

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var result = new AgentRunLlmStepResult
            {
                Content = "The exact connected-service effect is ready.",
                AccumulatedText = "The exact connected-service effect is ready.",
                FinishReason = "tool_calls",
                HasStreamedTextContent = true,
            };
            result.ToolCalls.Add(ToolCall.Clone());
            var continuation = new AgentRunNextLlmStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                LlmStepResult = result,
            };
            var capability = new AgentRunAuthorizedToolStep(
                request.RunId,
                request.Request.CorrelationId,
                request.Attempt,
                continuation.StepIndex,
                [ToolCall],
                _ =>
                {
                    ToolExecutions++;
                    return Task.FromResult(new AgentRunToolStepResult
                    {
                        AdvanceRound = false,
                        ResultMessages =
                        {
                            new AgentRunChatMessage
                            {
                                Role = "tool",
                                ToolCallId = ToolCall.Id,
                                Content = "{\"approval_required\":true}",
                            },
                        },
                        ToolReceipts =
                        {
                            new AgentToolReceipt
                            {
                                CallId = ToolCall.Id,
                                ToolName = ToolCall.Name,
                                Status = AgentToolReceiptStatus.ApprovalRequired,
                                Effect = AgentToolReceiptEffect.Mutating,
                                ApprovalRequestId = "tool_approval",
                                ErrorCode = "NYXID_APPROVAL_REQUIRED",
                                ErrorMessage = "NyxID approval is required.",
                                NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.PerRequest,
                            },
                        },
                    });
                });
            return Task.FromResult(new AgentRunLlmStepExecution(
                continuation,
                capability,
                [
                    new AgentRunAuthorizedToolCallSafety(
                        ToolCall.Id,
                        ToolCall.Name,
                        ToolCall.ArgumentsJson,
                        new AgentToolCallSafety(
                            RequiresApproval: false,
                            IsReadOnly: false,
                            IsDestructive: false),
                        SideEffectKind: "lark.approval.create",
                        OperationAdmission: admission.Clone()),
                ]));
        }

        public async Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct)
        {
            var result = authorizedToolStep?.Matches(request) == true
                ? await authorizedToolStep.ExecuteAsync(ct)
                : new AgentRunToolStepResult { AdvanceRound = false };
            return new AgentRunNextToolStepRequestedEvent
            {
                RunId = request.RunId,
                CorrelationId = request.Request.CorrelationId,
                TargetActorId = request.Request.TargetActorId,
                Attempt = request.Attempt,
                StepIndex = request.StepIndex + 1,
                Request = request.Request.Clone(),
                ToolStepResult = result,
            };
        }
    }
}
