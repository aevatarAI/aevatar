using System.Reflection;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Type = System.Type;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatRecoveryAndSecurityTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Activation_WithRequestedPostcondition_ShouldOnlySignalSelfForRecovery()
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistStateAsync(eventStore, actorId, CreateRequestedPostconditionState());
        using var services = BuildEventSourcingServices(eventStore);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateController(services, actorId, runtime, dispatch);

        await agent.ActivateAsync();

        runtime.CreateCalls.Should().BeEmpty(
            "activation recovery must first re-enter the conversation inbox");
        runtime.LinkCalls.Should().BeEmpty();
        var recovery = dispatch.Calls.Should().ContainSingle().Which;
        recovery.ActorId.Should().Be(actorId);
        recovery.Envelope.Route.PublisherActorId.Should().Be(actorId);
        recovery.Envelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        recovery.Envelope.Payload.TypeUrl.Should().EndWith(
            "/aevatar.gagents.nyxid_chat.NyxIdChatRecoveryRequestedSignal");
        recovery.Envelope.Payload.TypeUrl.Should().NotEndWith(
            "/aevatar.gagents.nyxid_chat.NyxIdChatOperationDispatchCommand");
        var signal = recovery.Envelope.Payload.Unpack<NyxIdChatRecoveryRequestedSignal>();
        signal.Kind.Should().Be(NyxIdChatRecoveryKind.PostconditionRedispatch);
        signal.ExpectedStateVersion.Should().Be(1);
        signal.Key.Should().BeEquivalentTo(
            agent.State.ActiveTask.Steps.Single().Operation.Key);
    }

    [Fact]
    public async Task RecoverySignal_WithExactRequestedPostcondition_ShouldRedispatchSameOperation()
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistStateAsync(eventStore, actorId, CreateRequestedPostconditionState());
        using var services = BuildEventSourcingServices(eventStore);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateController(services, actorId, runtime, dispatch);
        await agent.ActivateAsync();
        var recoveryEnvelope = dispatch.Calls.Single().Envelope.Clone();
        var recovery = recoveryEnvelope.Payload
            .Unpack<NyxIdChatRecoveryRequestedSignal>();
        dispatch.Calls.Clear();

        await agent.HandleEventAsync(recoveryEnvelope);

        var turnActorId = NyxIdChatTurnActorIds.ForTurn(actorId, recovery.Key.TurnId);
        runtime.CreateCalls.Should().ContainSingle().Which.Should().Be(
            (typeof(NyxIdChatTurnGAgent), turnActorId));
        runtime.LinkCalls.Should().ContainSingle().Which.Should().Be((actorId, turnActorId));
        var operationDelivery = dispatch.Calls.Should().ContainSingle().Which;
        operationDelivery.ActorId.Should().Be(turnActorId);
        var operation = operationDelivery.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        operation.Key.Should().BeEquivalentTo(recovery.Key);
        operation.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ActionPostcondition);
        operation.ActionPostcondition.ActionRequestId.Should().Be("action-alpha");
        operation.ActionPostcondition.OwnerSubject.Should().Be("owner-alpha");
        operation.ActionPostcondition.ResourceHint.UserService.UserServiceId.Should().Be(
            "service-alpha");
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);
        var events = await eventStore.GetEventsAsync(actorId);
        events.Should().HaveCount(2);
        events[^1].EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task Activation_WithInterruptedLlm_ShouldOnlySignalTypedReconciliation()
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistStateAsync(eventStore, actorId, CreateInterruptedLlmState());
        using var services = BuildEventSourcingServices(eventStore);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateController(services, actorId, runtime, dispatch);

        await agent.ActivateAsync();

        runtime.CreateCalls.Should().BeEmpty();
        runtime.LinkCalls.Should().BeEmpty();
        var recovery = dispatch.Calls.Should().ContainSingle().Which;
        recovery.ActorId.Should().Be(actorId);
        recovery.Envelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        var signal = recovery.Envelope.Payload.Unpack<NyxIdChatRecoveryRequestedSignal>();
        signal.Kind.Should().Be(
            NyxIdChatRecoveryKind.InterruptedOperationReconciliation);
        signal.ExpectedStateVersion.Should().Be(1);
        signal.Key.Should().BeEquivalentTo(
            agent.State.ActiveTask.Steps.Single().Operation.Key);
    }

    [Fact]
    public async Task InterruptedLlmRecovery_ShouldFailSafelyWithoutAutomaticReplay()
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistStateAsync(eventStore, actorId, CreateInterruptedLlmState());
        using var services = BuildEventSourcingServices(eventStore);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateController(services, actorId, runtime, dispatch);
        await agent.ActivateAsync();
        var recoveryEnvelope = dispatch.Calls.Single().Envelope.Clone();
        dispatch.Calls.Clear();

        await agent.HandleEventAsync(recoveryEnvelope);

        runtime.CreateCalls.Should().BeEmpty();
        runtime.LinkCalls.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty("recovery must not replay model I/O");
        var step = agent.State.ActiveTask.Steps.Should().ContainSingle().Which;
        step.Status.Should().Be(NyxIdChatStepStatus.Failed);
        step.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Failed);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        step.FailureCode.Should().Be("NYXID_CHAT_OPERATION_INTERRUPTED");
        step.AvailableActions.Retry.Should().BeTrue(
            "a new authenticated command may explicitly authorize retry");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        var events = await eventStore.GetEventsAsync(actorId);
        events.Should().HaveCount(2);
        events[^1].EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task InterruptedEffectfulToolRecovery_ShouldBecomeUncertainWithoutReplay()
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistStateAsync(eventStore, actorId, CreateInterruptedToolState());
        using var services = BuildEventSourcingServices(eventStore);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateController(services, actorId, runtime, dispatch);
        await agent.ActivateAsync();
        var recoveryEnvelope = dispatch.Calls.Single().Envelope.Clone();
        dispatch.Calls.Clear();

        await agent.HandleEventAsync(recoveryEnvelope);

        runtime.CreateCalls.Should().BeEmpty();
        runtime.LinkCalls.Should().BeEmpty();
        dispatch.Calls.Should().NotContain(call =>
            call.Envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor),
            "recovery must not repeat an effect-capable tool");
        dispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor),
            "the committed failed turn must continue to transcript delivery");
        var step = agent.State.ActiveTask.Steps.Should().ContainSingle().Which;
        step.Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        step.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Uncertain);
        step.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        step.AvailableActions.Retry.Should().BeFalse();
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RecoverySignal_WithStaleVersionOrKey_ShouldNoOp(
        bool staleVersion,
        bool staleKey)
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistStateAsync(eventStore, actorId, CreateInterruptedToolState());
        using var services = BuildEventSourcingServices(eventStore);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateController(services, actorId, runtime, dispatch);
        await agent.ActivateAsync();
        var envelope = dispatch.Calls.Single().Envelope.Clone();
        var signal = envelope.Payload.Unpack<NyxIdChatRecoveryRequestedSignal>();
        if (staleVersion)
            signal.ExpectedStateVersion++;
        if (staleKey)
            signal.Key.OperationGeneration++;
        envelope.Payload = Any.Pack(signal);
        dispatch.Calls.Clear();

        await agent.HandleEventAsync(envelope);

        runtime.CreateCalls.Should().BeEmpty();
        runtime.LinkCalls.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
        agent.State.ActiveTask.Steps.Single().Status.Should().Be(
            NyxIdChatStepStatus.Running);
        (await eventStore.GetEventsAsync(actorId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Activation_WithBlockedBrowserAction_ShouldNotStartHiddenContinuation()
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistStateAsync(eventStore, actorId, CreateBlockedBrowserActionState());
        using var services = BuildEventSourcingServices(eventStore);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateController(services, actorId, runtime, dispatch);

        await agent.ActivateAsync();

        runtime.CreateCalls.Should().BeEmpty();
        runtime.LinkCalls.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
        (await eventStore.GetEventsAsync(actorId)).Should().ContainSingle();
    }

    [Fact]
    public void TurnRecoveryWaterline_ShouldPersistTypedExternalEffectRisk()
    {
        NyxIdChatTurnGAgentState.Descriptor.FindFieldByName("may_change_external_state")
            .Should().NotBeNull();
        NyxIdChatTurnOperationAdmittedEvent.Descriptor
            .FindFieldByName("may_change_external_state")
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData(NyxIdChatStepKind.Llm, NyxIdChatEffectEvidence.NotApplied)]
    [InlineData(NyxIdChatStepKind.Tool, NyxIdChatEffectEvidence.MayHaveChanged)]
    public async Task TurnActivation_WithAdmittedOperation_ShouldReconcileWithoutExecutingAgain(
        NyxIdChatStepKind operationKind,
        NyxIdChatEffectEvidence expectedEffect)
    {
        const string turnActorId = "turn-actor-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTurnAdmissionAsync(eventStore, turnActorId, operationKind);
        using var services = BuildEventSourcingServices(eventStore);
        var executor = new RecordingTurnOperationExecutor();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateTurnActor(services, turnActorId, executor, dispatch);

        await agent.ActivateAsync();

        executor.Commands.Should().BeEmpty("activation must never replay provider or tool I/O");
        var recoveryEnvelope = dispatch.Calls.Should().ContainSingle().Which.Envelope.Clone();
        recoveryEnvelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        var recovery = recoveryEnvelope.Payload.Unpack<NyxIdChatRecoveryRequestedSignal>();
        recovery.Kind.Should().Be(
            NyxIdChatRecoveryKind.InterruptedOperationReconciliation);
        recovery.ExpectedStateVersion.Should().Be(1);
        dispatch.Calls.Clear();

        await agent.HandleEventAsync(recoveryEnvelope);

        executor.Commands.Should().BeEmpty();
        var delivery = dispatch.Calls.Should().ContainSingle().Which;
        delivery.ActorId.Should().Be("conversation-alpha");
        var result = delivery.Envelope.Payload.Unpack<NyxIdChatOperationResultSignal>();
        result.Key.Should().BeEquivalentTo(recovery.Key);
        result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        result.Failure.ExternalEffect.Should().Be(expectedEffect);
        agent.State.ResultDelivered.Should().BeTrue();
        agent.State.Phase.Should().Be(expectedEffect == NyxIdChatEffectEvidence.MayHaveChanged
            ? NyxIdChatOperationPhase.Uncertain
            : NyxIdChatOperationPhase.Failed);
        (await eventStore.GetEventsAsync(turnActorId)).Should().HaveCount(3);
    }

    [Fact]
    public async Task TurnActivation_WithCompletedUndeliveredResult_ShouldReportDeliveryLossWithoutReplay()
    {
        const string turnActorId = "turn-actor-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistCompletedUndeliveredTurnAsync(eventStore, turnActorId);
        using var services = BuildEventSourcingServices(eventStore);
        var executor = new RecordingTurnOperationExecutor();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateTurnActor(services, turnActorId, executor, dispatch);

        await agent.ActivateAsync();

        executor.Commands.Should().BeEmpty("recovery cannot reconstruct or repeat the original I/O");
        var recoveryEnvelope = dispatch.Calls.Should().ContainSingle().Which.Envelope.Clone();
        var recovery = recoveryEnvelope.Payload.Unpack<NyxIdChatRecoveryRequestedSignal>();
        recovery.ExpectedStateVersion.Should().Be(2);
        dispatch.Calls.Clear();

        await agent.HandleEventAsync(recoveryEnvelope);

        executor.Commands.Should().BeEmpty();
        var result = dispatch.Calls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationResultSignal>();
        result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        result.Failure.FailureCode.Should().Be(
            "NYXID_CHAT_OPERATION_RESULT_DELIVERY_LOST");
        result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.Confirmed,
            "recovery must preserve the child actor's committed effect evidence");
        agent.State.ResultDelivered.Should().BeTrue();
        agent.State.Phase.Should().Be(NyxIdChatOperationPhase.Failed);
        agent.State.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        (await eventStore.GetEventsAsync(turnActorId)).Should().HaveCount(4);
    }

    [Fact]
    public async Task RepeatedActivation_ShouldRestoreByteEquivalentSnapshotWithoutOperationIo()
    {
        const string actorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistStateAsync(eventStore, actorId, CreateRequestedPostconditionState());
        using var services = BuildEventSourcingServices(eventStore);
        var firstRuntime = new RecordingActorRuntime();
        var firstDispatch = new RecordingActorDispatchPort();
        var first = CreateController(services, actorId, firstRuntime, firstDispatch);
        await first.ActivateAsync();
        var firstSnapshot = first.State.ToByteString();

        var secondRuntime = new RecordingActorRuntime();
        var secondDispatch = new RecordingActorDispatchPort();
        var second = CreateController(services, actorId, secondRuntime, secondDispatch);
        await second.ActivateAsync();

        second.State.ToByteString().Should().Equal(firstSnapshot);
        firstRuntime.CreateCalls.Should().BeEmpty();
        secondRuntime.CreateCalls.Should().BeEmpty();
        firstDispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor));
        secondDispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor));
        (await eventStore.GetEventsAsync(actorId)).Should().ContainSingle(
            "activation recovery signals are not committed product facts");
    }

    [Fact]
    public async Task TransientCredential_ShouldStayOutOfActorReadModelAndAguiFrames()
    {
        const string actorId = "conversation-alpha";
        const string secret = "credential-marker-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateController(services, actorId, runtime, dispatch);
        await agent.ActivateAsync();
        var start = new NyxIdChatStartTurnCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = actorId,
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            ClientRequestId = "client-alpha",
            CommandId = "command-alpha",
            CorrelationId = "correlation-alpha",
            Prompt = "Use the connected service.",
            LlmControl = new LLMControlContextPayload
            {
                NyxIdAccessToken = secret,
            },
            ToolContext = new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload
                {
                    NyxIdAccessToken = secret,
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope(actorId, start));

        var transientCommand = dispatch.Calls.Single(call =>
                call.Envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor))
            .Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        transientCommand.Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(secret);
        transientCommand.Llm.Request.ToolContext.Credentials.NyxIdAccessToken.Should().Be(secret);
        AssertSecretAbsent(agent.State, secret);

        var projectionWrites = new RecordingProjectionWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            projectionWrites,
            new FixedProjectionClock(FixedNow));
        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = actorId,
                ProjectionKind = NyxIdChatConversationGAgent.ProjectionKind,
            },
            WrapCommittedState(agent.State, stateVersion: 2),
            CancellationToken.None);
        AssertSecretAbsent(projectionWrites.Upserts.Should().ContainSingle().Which, secret);

        var frameState = agent.State.Clone();
        frameState.PendingHistoryInitialization = new NyxIdChatHistoryInitializationOutbox
        {
            OperationId = "agui-initialization-outbox-sentinel",
            ScopeId = "scope-alpha",
            ConversationId = actorId,
            ServiceId = actorId,
            ServiceKind = NyxIdChatServiceDefaults.GAgentKind,
            InitialTitle = "agui-credential-outbox-sentinel",
            CreatedAt = Timestamp.FromDateTimeOffset(FixedNow),
            Attempt = 1,
        };
        frameState.HistoryDeliveryReservation = new NyxIdChatHistoryDeliveryReservationState
        {
            DeliveryId = "agui-reservation-outbox-sentinel",
            ScopeId = "scope-alpha",
            ConversationId = actorId,
            TurnId = "turn-alpha",
            UserText = "agui-credential-outbox-sentinel",
            SourceActorId = actorId,
            SourceCommandId = "command-alpha",
            RequestFingerprint = "fingerprint-alpha",
            CreateConversationIfMissing = true,
        };
        frameState.PendingHistoryTerminal = new NyxIdChatHistoryTerminalOutbox
        {
            DeliveryId = "agui-reservation-outbox-sentinel",
            TurnId = "turn-alpha",
            SourceActorId = actorId,
            SourceCommandId = "command-alpha",
            Status = NyxIdChatTurnStatus.Blocked,
            Text = "agui-terminal-outbox-sentinel agui-credential-outbox-sentinel",
            ErrorCode = "SAFE_BLOCKED",
            ObservedAt = Timestamp.FromDateTimeOffset(FixedNow),
            Attempt = 1,
        };
        var frames = NyxIdChatConversationAguiFrameBuilder.BuildStarted(
            actorId,
            "turn-alpha",
            frameState);
        frames.Should().NotBeEmpty();
        frames.Should().OnlyContain(frame => !ContainsSecret(frame, secret));
        var frameBytes = Encoding.UTF8.GetString(
            frames.SelectMany(static frame => frame.ToByteArray()).ToArray());
        frameBytes.Should()
            .NotContain("agui-initialization-outbox-sentinel")
            .And.NotContain("agui-reservation-outbox-sentinel")
            .And.NotContain("agui-terminal-outbox-sentinel")
            .And.NotContain("agui-credential-outbox-sentinel");
    }

    private static NyxIdChatConversationGAgentState CreateRequestedPostconditionState()
    {
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-continuation-alpha",
            TaskId = "task-continuation-alpha",
            StepId = "step-postcondition-alpha",
            OperationId = "operation-postcondition-alpha",
            OperationGeneration = 1,
        };
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = key.TurnId,
                TaskId = key.TaskId,
                ClientRequestId = "client-action-alpha",
                Status = NyxIdChatTurnStatus.Active,
                CreatedAt = Timestamp.FromDateTimeOffset(FixedNow),
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = key.TurnId,
                TaskId = key.TaskId,
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = key.StepId,
                ActiveOperationId = key.OperationId,
                CreatedAt = Timestamp.FromDateTimeOffset(FixedNow),
                UpdatedAt = Timestamp.FromDateTimeOffset(FixedNow),
            },
            ContinuationAdmission = new NyxIdChatContinuationAdmissionState
            {
                Kind = NyxIdChatContinuationKind.Action,
                RequestId = "command-action-alpha",
                ClientRequestId = "client-action-alpha",
                OriginTurnId = "turn-origin-alpha",
                ContinuationTurnId = key.TurnId,
                Status = NyxIdChatContinuationAdmissionStatus.Accepted,
                OwnerSubject = "owner-alpha",
                CommittedAt = Timestamp.FromDateTimeOffset(FixedNow),
            },
            ProgressSequence = 7,
            UpdatedAt = Timestamp.FromDateTimeOffset(FixedNow),
            HistoryDeliveryReservation = new NyxIdChatHistoryDeliveryReservationState
            {
                DeliveryId = "delivery-continuation-alpha",
                ScopeId = "scope-alpha",
                ConversationId = "conversation-alpha",
                TurnId = key.TurnId,
                UserText = "Continue after the approved action.",
                SourceActorId = "conversation-alpha",
                SourceCommandId = "command-action-alpha",
                SourceCorrelationId = "correlation-action-alpha",
                RequestFingerprint = "fingerprint-continuation-alpha",
                CreateConversationIfMissing = true,
                Dispatched = true,
                DispatchedAt = Timestamp.FromDateTimeOffset(FixedNow),
            },
        };
        state.LatestTurn = state.ActiveTurn.Clone();
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = "Verify the typed NyxID action postcondition.",
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    ActionRequestId = "action-alpha",
                    PostconditionKind = nameof(NyxIdAssistantActionKind.ServiceConnect),
                },
            },
            ActionRequestId = "action-alpha",
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = key,
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = Timestamp.FromDateTimeOffset(FixedNow),
            },
            UpdatedAt = Timestamp.FromDateTimeOffset(FixedNow),
        });
        var report = new NyxIdChatActionReport
        {
            ActionRequestId = "action-alpha",
            OriginTurnId = "turn-origin-alpha",
            Disposition = NyxIdChatActionDisposition.Completed,
            Resource = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = "service-alpha",
                },
            },
            ReportedAt = Timestamp.FromDateTimeOffset(FixedNow),
        };
        state.ContinuationAdmission.ActionReports.Add(report.Clone());
        state.PendingActions.Add(new NyxIdChatActionRequestState
        {
            SchemaVersion = 4,
            RegistryRevision = "nyxid-assistant-actions.v4",
            ConversationActorId = state.ConversationActorId,
            OriginTurnId = report.OriginTurnId,
            TaskId = "task-origin-alpha",
            StepId = "step-browser-action-alpha",
            ActionRequestId = report.ActionRequestId,
            Action = NyxIdAssistantActionKind.ServiceConnect,
            Params = new NyxIdAssistantActionParams
            {
                CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
                {
                    ServiceSlug = "api-github",
                },
            },
            Reports = { report.Clone() },
            RequestedAt = Timestamp.FromDateTimeOffset(FixedNow),
        });
        return state;
    }

    private static NyxIdChatConversationGAgentState CreateInterruptedLlmState()
    {
        var state = CreateRequestedPostconditionState();
        state.ContinuationAdmission = null;
        state.PendingActions.Clear();
        var step = state.ActiveTask.Steps.Single();
        step.Kind = NyxIdChatStepKind.Llm;
        step.Source = new NyxIdChatStepSource
        {
            Llm = new NyxIdChatLLMStepSource { Model = "model-alpha" },
        };
        step.ActionRequestId = string.Empty;
        step.RetryInputRebuildable = true;
        step.MayChangeExternalState = false;
        step.Operation.Kind = NyxIdChatStepKind.Llm;
        step.Operation.Phase = NyxIdChatOperationPhase.Dispatched;
        step.Operation.MayChangeExternalState = false;
        return state;
    }

    private static NyxIdChatConversationGAgentState CreateInterruptedToolState()
    {
        var state = CreateInterruptedLlmState();
        var step = state.ActiveTask.Steps.Single();
        step.Kind = NyxIdChatStepKind.Tool;
        step.Source = new NyxIdChatStepSource
        {
            Tool = new NyxIdChatToolStepSource { ToolName = "tool-alpha" },
        };
        step.RetryInputRebuildable = false;
        step.MayChangeExternalState = true;
        step.Operation.Kind = NyxIdChatStepKind.Tool;
        step.Operation.MayChangeExternalState = true;
        return state;
    }

    private static NyxIdChatConversationGAgentState CreateBlockedBrowserActionState()
    {
        var state = CreateRequestedPostconditionState();
        state.ContinuationAdmission = null;
        state.ActiveTurn.Status = NyxIdChatTurnStatus.Blocked;
        state.ActiveTurn.TerminalAt = Timestamp.FromDateTimeOffset(FixedNow);
        state.LatestTurn = state.ActiveTurn.Clone();
        state.ActiveTask.Status = NyxIdChatTaskStatus.Blocked;
        state.ActiveTask.ActiveOperationId = string.Empty;
        var step = state.ActiveTask.Steps.Single();
        step.Kind = NyxIdChatStepKind.BrowserAction;
        step.Status = NyxIdChatStepStatus.Waiting;
        step.Source = new NyxIdChatStepSource
        {
            BrowserAction = new NyxIdChatBrowserActionStepSource
            {
                Action = NyxIdAssistantActionKind.ServiceConnect,
                ActionRequestId = "action-alpha",
            },
        };
        step.Operation = null;
        return state;
    }

    private static async Task PersistStateAsync(
        IEventStore eventStore,
        string actorId,
        NyxIdChatConversationGAgentState state)
    {
        await eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "recovery-state-alpha",
                    AgentId = actorId,
                    Version = 1,
                    Timestamp = Timestamp.FromDateTimeOffset(FixedNow),
                    EventType = NyxIdChatTurnStartedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnStartedEvent
                    {
                        State = state.Clone(),
                    }),
                },
            ],
            expectedVersion: 0);
    }

    private static Task PersistTurnAdmissionAsync(
        IEventStore eventStore,
        string actorId,
        NyxIdChatStepKind operationKind) =>
        eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "turn-admission-alpha",
                    AgentId = actorId,
                    Version = 1,
                    Timestamp = Timestamp.FromDateTimeOffset(FixedNow),
                    EventType = NyxIdChatTurnOperationAdmittedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnOperationAdmittedEvent
                    {
                        Key = CreateOperationKey(),
                        OperationKind = operationKind,
                        MayChangeExternalState = operationKind == NyxIdChatStepKind.Tool,
                        AdmittedAt = Timestamp.FromDateTimeOffset(FixedNow),
                    }),
                },
            ],
            expectedVersion: 0);

    private static Task PersistCompletedUndeliveredTurnAsync(
        IEventStore eventStore,
        string actorId)
    {
        var key = CreateOperationKey();
        return eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "turn-admission-alpha",
                    AgentId = actorId,
                    Version = 1,
                    Timestamp = Timestamp.FromDateTimeOffset(FixedNow),
                    EventType = NyxIdChatTurnOperationAdmittedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnOperationAdmittedEvent
                    {
                        Key = key.Clone(),
                        OperationKind = NyxIdChatStepKind.Tool,
                        MayChangeExternalState = true,
                        AdmittedAt = Timestamp.FromDateTimeOffset(FixedNow),
                    }),
                },
                new StateEvent
                {
                    EventId = "turn-completed-alpha",
                    AgentId = actorId,
                    Version = 2,
                    Timestamp = Timestamp.FromDateTimeOffset(FixedNow),
                    EventType = NyxIdChatTurnOperationCompletedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnOperationCompletedEvent
                    {
                        Key = key.Clone(),
                        Phase = NyxIdChatOperationPhase.Succeeded,
                        ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                        CompletedAt = Timestamp.FromDateTimeOffset(FixedNow),
                    }),
                },
            ],
            expectedVersion: 0);
    }

    private static NyxIdChatOperationKey CreateOperationKey() => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-alpha",
        OperationId = "operation-alpha",
        OperationGeneration = 1,
    };

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) => new()
    {
        Id = "recovery-envelope-alpha",
        Timestamp = Timestamp.FromDateTimeOffset(FixedNow),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute
        {
            Direct = new DirectRoute { TargetActorId = actorId },
        },
        Propagation = new EnvelopePropagation
        {
            CorrelationId = "recovery-correlation-alpha",
        },
    };

    private static EventEnvelope WrapCommittedState(
        NyxIdChatConversationGAgentState state,
        long stateVersion) => new()
    {
        Id = "committed-state-alpha",
        Timestamp = Timestamp.FromDateTimeOffset(FixedNow),
        Route = EnvelopeRouteSemantics.CreateObserverPublication(
            state.ConversationActorId),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = "committed-state-alpha",
                AgentId = state.ConversationActorId,
                Version = stateVersion,
                Timestamp = Timestamp.FromDateTimeOffset(FixedNow),
                EventType = NyxIdChatOperationDispatchedEvent.Descriptor.FullName,
                EventData = Any.Pack(new NyxIdChatOperationDispatchedEvent
                {
                    Key = state.ActiveTask.Steps.Single().Operation.Key.Clone(),
                    DispatchedAt = Timestamp.FromDateTimeOffset(FixedNow),
                }),
            },
            StateRoot = Any.Pack(state),
        }),
    };

    private static void AssertSecretAbsent(IMessage message, string secret) =>
        ContainsSecret(message, secret).Should().BeFalse();

    private static bool ContainsSecret(IMessage message, string secret) =>
        Encoding.UTF8.GetString(message.ToByteArray())
            .Contains(secret, StringComparison.Ordinal);

    private static NyxIdChatConversationGAgent CreateController(
        ServiceProvider services,
        string actorId,
        IActorRuntime runtime,
        IActorDispatchPort dispatch)
    {
        var agent = new NyxIdChatConversationGAgent(
            runtime,
            dispatch,
            new FixedTimeProvider(FixedNow))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);
        return agent;
    }

    private static NyxIdChatTurnGAgent CreateTurnActor(
        ServiceProvider services,
        string actorId,
        INyxIdChatTurnOperationExecutor executor,
        IActorDispatchPort dispatch)
    {
        var agent = new NyxIdChatTurnGAgent(
            executor,
            dispatch,
            new FixedTimeProvider(FixedNow))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatTurnGAgentState>>(),
        };
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);
        return agent;
    }

    private static ServiceProvider BuildEventSourcingServices(IEventStore eventStore) =>
        new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
            .AddSingleton<IChatHistoryCommandPort, NoopChatHistoryCommandPort>()
            .AddTransient(
                typeof(IEventSourcingBehaviorFactory<>),
                typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

    private sealed class NoopChatHistoryCommandPort : IChatHistoryCommandPort
    {
        public Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ReserveTurnDeliveryAsync(
            ChatHistoryTurnDeliveryReservation request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyTurnTerminalAsync(
            ChatHistoryTurnTerminalNotification notification,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default) =>
            Task.FromResult(ChatHistoryDeleteResult.Accepted());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<(Type Type, string Id)> CreateCalls { get; } = [];
        public List<(string ParentId, string ChildId)> LinkCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(
            Type agentType,
            string? id = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreateCalls.Add((agentType, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(
            string parentId,
            string childId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LinkCalls.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingTurnOperationExecutor : INyxIdChatTurnOperationExecutor
    {
        public List<NyxIdChatOperationDispatchCommand> Commands { get; } = [];

        public Task<NyxIdChatTurnOperationExecution> ExecuteAsync(
            NyxIdChatOperationDispatchCommand command,
            NyxIdChatTransientExecutionSession session,
            Func<NyxIdChatOperationProgressSignal, CancellationToken, Task> reportProgressAsync,
            CancellationToken ct)
        {
            _ = session;
            _ = reportProgressAsync;
            ct.ThrowIfCancellationRequested();
            Commands.Add(command.Clone());
            return Task.FromResult(new NyxIdChatTurnOperationExecution(
                new NyxIdChatOperationResultSignal
                {
                    Key = command.Key.Clone(),
                    Llm = new NyxIdChatLLMOperationResult(),
                }));
        }
    }

    private sealed class RecordingProjectionWriteDispatcher
        : IProjectionWriteDispatcher<NyxIdChatConversationCurrentStateDocument>
    {
        public List<NyxIdChatConversationCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            NyxIdChatConversationCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new RecordingAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent : IAgent
    {
        public string Id => "recording-agent";
        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
