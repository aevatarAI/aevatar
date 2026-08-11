using System.Reflection;
using System.Security.Claims;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AGUI.Contracts;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Core;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatConversationGAgentTests
{
    private const string P0CActorId = "conversation-alpha";
    private const string P0CScopeId = "scope-alpha";
    private const string P0COwnerSubject = "owner-alpha";
    private const string P0COriginTurnId = "turn-alpha";
    private const string P0CActionRequestId = "action-key-alpha";
    private const string P0CKeyId = "key-alpha";
    private const string P0CRawBearer = "raw-bearer-sentinel-p0c";

    private static readonly DateTimeOffset P0CNow =
        new(2026, 8, 12, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthenticatedKeyCreateContinuation_ShouldReconcileThroughRealActor()
    {
        var clock = new FakeTimeProvider(P0CNow);
        var vault = new InMemorySecretVault(clock);
        var authorityPort = P0CCreateAuthorityPort(vault, clock);
        var endpoint = await P0CIssueEndpointContinuationAsync(authorityPort);
        var eventStore = new InMemoryEventStoreForTests();
        await P0CPersistBlockedKeyCreateAsync(eventStore, clock.GetUtcNow());
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, secretVault: vault);
        var actor = CreateController(services, P0CActorId, dispatch, timeProvider: clock);
        await actor.ActivateAsync();
        actor.State.PendingActions.Should().ContainSingle(action =>
            action.ActionRequestId == P0CActionRequestId &&
            action.Action == NyxIdAssistantActionKind.KeyCreate);
        actor.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        actor.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.Postcondition &&
            step.ActionRequestId == P0CActionRequestId);

        await actor.HandleEventAsync(endpoint.Envelope);

        var committedAfterAdmission = await eventStore.GetEventsAsync(P0CActorId);
        var committedTypes = string.Join(
            ", ",
            committedAfterAdmission.Select(static item =>
            {
                if (!item.EventData.Is(NyxIdChatTurnAdmissionRejectedEvent.Descriptor))
                    return item.EventType;

                var rejection = item.EventData.Unpack<NyxIdChatTurnAdmissionRejectedEvent>();
                return $"{item.EventType}({rejection.ReasonCode}: {rejection.SafeMessage})";
            }));
        var admissionStateEvent = committedAfterAdmission.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor),
                "the actual committed event types were {0}",
                committedTypes)
            .Which;
        var admission = admissionStateEvent.EventData
            .Unpack<NyxIdChatContinuationAdmissionCommittedEvent>();
        admission.Admission.ReadAuthority.Should().BeEquivalentTo(endpoint.Command.ReadAuthority);
        admission.State.ContinuationAdmission.ReadAuthority.Should()
            .BeEquivalentTo(endpoint.Command.ReadAuthority);
        var admittedStep = admission.State.ActiveTask.Steps.Should()
            .ContainSingle(step => step.Kind == NyxIdChatStepKind.Postcondition).Which;
        admittedStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);

        var operation = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        operation.Key.Should().BeEquivalentTo(admittedStep.Operation.Key);
        operation.ActionPostcondition.Action.Should().Be(NyxIdAssistantActionKind.KeyCreate);
        operation.ActionPostcondition.ReadAuthority.Should()
            .BeEquivalentTo(endpoint.Command.ReadAuthority);
        var evidence = P0CCreateEvidence(clock.GetUtcNow());
        var execution = await P0CExecutePostconditionAsync(
            operation,
            authorityPort,
            clock,
            evidence);

        await actor.HandleEventAsync(CreateEnvelope(P0CActorId, execution.Result));

        evidence.BearerTokens.Should().ContainSingle().Which.Should().Be(P0CRawBearer);
        evidence.KeyIds.Should().ContainSingle().Which.Should().Be(P0CKeyId);
        var committed = await eventStore.GetEventsAsync(P0CActorId);
        var reconciledStateEvent = committed.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Which;
        var reconciled = reconciledStateEvent.EventData
            .Unpack<NyxIdChatOperationReconciledEvent>();
        reconciled.Result.ActionPostcondition.Verified.Should().BeTrue();
        reconciled.Result.ActionPostcondition.Resource.Key.KeyId.Should().Be(P0CKeyId);
        reconciled.State.PendingActions.Should().BeEmpty();
        reconciled.State.RecentActions.Should().ContainSingle(action =>
            action.ActionRequestId == P0CActionRequestId &&
            action.PostconditionResult.Verified);
        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        reconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        actor.State.Should().BeEquivalentTo(reconciled.State);
    }

    [Fact]
    public async Task UnexpiredAuthority_AfterRealActorReactivation_ShouldRecoverAndReconcile()
    {
        var clock = new FakeTimeProvider(P0CNow);
        var vault = new InMemorySecretVault(clock);
        var endpointAuthorityPort = P0CCreateAuthorityPort(vault, clock);
        var endpoint = await P0CIssueEndpointContinuationAsync(endpointAuthorityPort);
        var eventStore = new InMemoryEventStoreForTests();
        await P0CPersistBlockedKeyCreateAsync(eventStore, clock.GetUtcNow());
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var interruptedDispatch = new RecordingActorDispatchPort(
            [],
            static (_, envelope) => envelope.Payload.Is(
                    NyxIdChatOperationDispatchCommand.Descriptor)
                ? Task.FromException(new OperationCanceledException(
                    "simulate process loss after durable admission"))
                : Task.CompletedTask);
        using var services = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: callbacks,
            secretVault: vault);
        var initial = CreateController(
            services,
            P0CActorId,
            interruptedDispatch,
            timeProvider: clock);
        await initial.ActivateAsync();

        var interrupted = () => initial.HandleEventAsync(endpoint.Envelope);
        await interrupted.Should().ThrowAsync<OperationCanceledException>();

        var initiallyDispatched = interruptedDispatch.OperationCalls.Should()
            .ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        var beforeRestart = await eventStore.GetEventsAsync(P0CActorId);
        beforeRestart.Should().ContainSingle(item =>
            item.EventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor));
        beforeRestart.Should().NotContain(item =>
            item.EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor));
        initial.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Postcondition)
            .Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);

        callbacks.TimeoutRequests.Clear();
        var recoveryDispatch = new RecordingActorDispatchPort(
            [],
            static (_, _) => Task.CompletedTask);
        var recovered = CreateController(
            services,
            P0CActorId,
            recoveryDispatch,
            timeProvider: clock);
        await recovered.ActivateAsync();
        var recoveryEnvelope = callbacks.TimeoutRequests.Should().ContainSingle(request =>
                request.TriggerEnvelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor))
            .Which.TriggerEnvelope.Clone();
        var recoverySignal = recoveryEnvelope.Payload.Unpack<NyxIdChatRecoveryRequestedSignal>();
        recoverySignal.Kind.Should().Be(NyxIdChatRecoveryKind.PostconditionRedispatch);
        recoverySignal.ExpectedStateVersion.Should().Be(beforeRestart[^1].Version);

        await recovered.HandleEventAsync(recoveryEnvelope);

        var recoveredOperation = recoveryDispatch.OperationCalls.Should()
            .ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        recoveredOperation.Key.Should().BeEquivalentTo(initiallyDispatched.Key);
        recoveredOperation.ActionPostcondition.ReadAuthority.Should()
            .BeEquivalentTo(endpoint.Command.ReadAuthority);
        var restartedAuthorityPort = P0CCreateAuthorityPort(vault, clock);
        var evidence = P0CCreateEvidence(clock.GetUtcNow());
        var execution = await P0CExecutePostconditionAsync(
            recoveredOperation,
            restartedAuthorityPort,
            clock,
            evidence);

        await recovered.HandleEventAsync(CreateEnvelope(P0CActorId, execution.Result));

        evidence.BearerTokens.Should().ContainSingle().Which.Should().Be(P0CRawBearer);
        var committed = await eventStore.GetEventsAsync(P0CActorId);
        committed.Should().Contain(item =>
            item.EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor));
        var reconciled = committed.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Which.EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        reconciled.State.PendingActions.Should().BeEmpty();
        reconciled.State.RecentActions.Should().ContainSingle(action =>
            action.ActionRequestId == P0CActionRequestId &&
            action.PostconditionResult.Verified);
        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        reconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
    }

    [Fact]
    public async Task ExpiredAuthority_ShouldPersistBlockedActorStateWithStableCode()
    {
        const string expectedExpiredCode = "NYXID_ACTION_READ_AUTHORITY_EXPIRED";

        var clock = new FakeTimeProvider(P0CNow);
        var vault = new InMemorySecretVault(clock);
        var authorityPort = P0CCreateAuthorityPort(vault, clock);
        var endpoint = await P0CIssueEndpointContinuationAsync(authorityPort);
        var eventStore = new InMemoryEventStoreForTests();
        await P0CPersistBlockedKeyCreateAsync(eventStore, clock.GetUtcNow());
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, secretVault: vault);
        var actor = CreateController(services, P0CActorId, dispatch, timeProvider: clock);
        await actor.ActivateAsync();
        await actor.HandleEventAsync(endpoint.Envelope);
        var operation = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        clock.Advance(TimeSpan.FromMinutes(11));
        var evidence = P0CCreateEvidence(clock.GetUtcNow());

        var execution = await P0CExecutePostconditionAsync(
            operation,
            authorityPort,
            clock,
            evidence);
        await actor.HandleEventAsync(CreateEnvelope(P0CActorId, execution.Result));

        evidence.BearerTokens.Should().BeEmpty();
        NyxIdActionReadAuthorityPort.ExpiredCode.Should().Be(expectedExpiredCode);
        execution.Result.ActionPostcondition.Verified.Should().BeFalse();
        execution.Result.ActionPostcondition.FailureCode.Should().Be(expectedExpiredCode);
        var committed = await eventStore.GetEventsAsync(P0CActorId);
        var reconciled = committed.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Which.EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        reconciled.Result.ActionPostcondition.FailureCode.Should().Be(expectedExpiredCode);
        reconciled.Task.FailureCode.Should().Be(expectedExpiredCode);
        reconciled.Turn.FailureCode.Should().Be(expectedExpiredCode);
        var reconciledTaskPostconditionStep = reconciled.Task.Steps.Should()
            .ContainSingle(step => step.Kind == NyxIdChatStepKind.Postcondition).Which;
        reconciledTaskPostconditionStep.FailureCode.Should().Be(expectedExpiredCode);
        reconciledTaskPostconditionStep.Operation.TerminalCode.Should().Be(expectedExpiredCode);
        reconciled.State.PendingActions.Should().ContainSingle().Which
            .PostconditionResult.FailureCode.Should().Be(expectedExpiredCode);
        reconciled.State.RecentActions.Should().BeEmpty();
        reconciled.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
        reconciled.State.ActiveTask.FailureCode.Should().Be(expectedExpiredCode);
        reconciled.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        reconciled.State.ActiveTurn.FailureCode.Should().Be(expectedExpiredCode);
        reconciled.State.LatestTurn.FailureCode.Should().Be(expectedExpiredCode);
        reconciled.State.RecentTerminalTurns.Should().ContainSingle(summary =>
                summary.TurnId == reconciled.State.ActiveTurn.TurnId).Which
            .FailureCode.Should().Be(expectedExpiredCode);
        var reconciledHistoryTerminal = reconciled.State.PendingHistoryTerminal;
        reconciledHistoryTerminal.Should().NotBeNull();
        reconciledHistoryTerminal!.ErrorCode.Should().Be(expectedExpiredCode);
        var postconditionStep = reconciled.State.ActiveTask.Steps.Should()
            .ContainSingle(step => step.Kind == NyxIdChatStepKind.Postcondition).Which;
        postconditionStep.Status.Should().Be(NyxIdChatStepStatus.Waiting);
        postconditionStep.FailureCode.Should().Be(expectedExpiredCode);
        postconditionStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Failed);
        postconditionStep.Operation.TerminalCode.Should().Be(expectedExpiredCode);
        actor.State.Should().BeEquivalentTo(reconciled.State);
        actor.State.PendingActions.Should().ContainSingle().Which
            .PostconditionResult.FailureCode.Should().Be(expectedExpiredCode);
        actor.State.ActiveTask.FailureCode.Should().Be(expectedExpiredCode);
        actor.State.ActiveTurn.FailureCode.Should().Be(expectedExpiredCode);
        actor.State.LatestTurn.FailureCode.Should().Be(expectedExpiredCode);
        actor.State.RecentTerminalTurns.Should().ContainSingle(summary =>
                summary.TurnId == actor.State.ActiveTurn.TurnId).Which
            .FailureCode.Should().Be(expectedExpiredCode);
        var actorHistoryTerminal = actor.State.PendingHistoryTerminal;
        actorHistoryTerminal.Should().NotBeNull();
        actorHistoryTerminal!.ErrorCode.Should().Be(expectedExpiredCode);
        var actorPostconditionStep = actor.State.ActiveTask.Steps.Should()
            .ContainSingle(step => step.Kind == NyxIdChatStepKind.Postcondition).Which;
        actorPostconditionStep.FailureCode.Should().Be(expectedExpiredCode);
        actorPostconditionStep.Operation.TerminalCode.Should().Be(expectedExpiredCode);
    }

    [Fact]
    public async Task RawBearer_ShouldRemainVaultOnlyAcrossActualActorAndProjectionSurfaces()
    {
        var clock = new FakeTimeProvider(P0CNow);
        var vault = new InMemorySecretVault(clock);
        var authorityPort = P0CCreateAuthorityPort(vault, clock);
        var endpoint = await P0CIssueEndpointContinuationAsync(authorityPort);
        var eventStore = new InMemoryEventStoreForTests();
        await P0CPersistBlockedKeyCreateAsync(eventStore, clock.GetUtcNow());
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, secretVault: vault);
        var actor = CreateController(services, P0CActorId, dispatch, timeProvider: clock);
        var publications = P0CAttachCommittedPublisher(actor);
        await actor.ActivateAsync();
        var committedBeforeAdmission = (await eventStore.GetEventsAsync(P0CActorId)).Count;
        await actor.HandleEventAsync(endpoint.Envelope);
        var operation = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        var evidence = P0CCreateEvidence(clock.GetUtcNow());
        var execution = await P0CExecutePostconditionAsync(
            operation,
            authorityPort,
            clock,
            evidence);
        await actor.HandleEventAsync(CreateEnvelope(P0CActorId, execution.Result));

        evidence.BearerTokens.Should().ContainSingle().Which.Should().Be(P0CRawBearer);
        P0CAssertSerializedBoundary(operation, P0CRawBearer, absent: true);
        P0CAssertSerializedBoundary(execution.Result, P0CRawBearer, absent: true);
        var opaqueRef = endpoint.Command.ReadAuthority!.SecretRef;
        var committed = await eventStore.GetEventsAsync(P0CActorId);
        var committedAfterAdmission = committed.Skip(committedBeforeAdmission).ToArray();
        committedAfterAdmission.Should().NotBeEmpty();
        foreach (var stateEvent in committedAfterAdmission)
            P0CAssertSerializedBoundary(stateEvent, P0CRawBearer, absent: true);
        publications.Publications.Count.Should().Be(committedAfterAdmission.Length);
        publications.Publications.Select(static item => item.StateEvent.EventId).Should()
            .Equal(committedAfterAdmission.Select(static item => item.EventId));
        foreach (var publication in publications.Publications)
            P0CAssertSerializedBoundary(publication, P0CRawBearer, absent: true);
        var admissionStateEvent = committed.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
            .Which;
        var admission = admissionStateEvent.EventData
            .Unpack<NyxIdChatContinuationAdmissionCommittedEvent>();
        var reconciledStateEvent = committed.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Which;
        var reconciled = reconciledStateEvent.EventData
            .Unpack<NyxIdChatOperationReconciledEvent>();
        var admissionObservation = publications.Publications.Should().ContainSingle(item =>
                item.StateEvent.EventData.Is(
                    NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
            .Which;
        var reconciledObservation = publications.Publications.Should().ContainSingle(item =>
                item.StateEvent.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Which;

        P0CAssertSerializedBoundary(endpoint.ActorCommand, P0CRawBearer, absent: true);
        P0CAssertSerializedBoundary(admission, P0CRawBearer, absent: true);
        P0CAssertSerializedBoundary(admissionObservation, P0CRawBearer, absent: true);
        P0CAssertSerializedBoundary(reconciled, P0CRawBearer, absent: true);
        P0CAssertSerializedBoundary(reconciledObservation, P0CRawBearer, absent: true);
        P0CAssertSerializedBoundary(actor.State, P0CRawBearer, absent: true);
        P0CAssertSerializedBoundary(actor.State.RecentActions.Single(), P0CRawBearer, absent: true);

        P0CAssertSerializedBoundary(admission, opaqueRef, absent: false);
        P0CAssertSerializedBoundary(admissionObservation, opaqueRef, absent: false);
        P0CAssertSerializedBoundary(reconciled, opaqueRef, absent: false);
        P0CAssertSerializedBoundary(reconciledObservation, opaqueRef, absent: false);
        P0CAssertSerializedBoundary(actor.State, opaqueRef, absent: false);
        P0CAssertSerializedBoundary(actor.State.RecentActions.Single(), opaqueRef, absent: true);

        var frames = NyxIdChatConversationAguiFrameBuilder.BuildContinuationChanged(
                P0CActorId,
                endpoint.ActorCommand.ContinuationTurnId,
                admission,
                admission.State.ProgressSequence)
            .Concat(NyxIdChatConversationAguiFrameBuilder.BuildReconciled(
                P0CActorId,
                endpoint.ActorCommand.ContinuationTurnId,
                reconciled))
            .ToArray();
        frames.Should().NotBeEmpty();
        frames.Should().OnlyContain(frame =>
            !P0CContainsSerialized(frame, P0CRawBearer) &&
            !P0CContainsSerialized(frame, opaqueRef));

        var dispatcher = new P0CProjectionWriteDispatcher();
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new P0CProjectionClock(clock.GetUtcNow()));
        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = P0CActorId,
                ProjectionKind = "nyxid-chat-conversation",
            },
            P0CWrapActualObservation(P0CActorId, reconciledObservation));

        var document = dispatcher.Upserts.Should().ContainSingle().Which;
        document.RecentActions.Should().ContainSingle().Which.ActionRequestId.Should()
            .Be(P0CActionRequestId);
        P0CAssertSerializedBoundary(document, P0CRawBearer, absent: true);
        P0CAssertSerializedBoundary(document, opaqueRef, absent: true);
    }

    private static async Task<P0CEndpointContinuation> P0CIssueEndpointContinuationAsync(
        NyxIdActionReadAuthorityPort authorityPort)
    {
        using var requestServices = new ServiceCollection()
            .AddSingleton<INyxIdActionReadAuthorityPort>(authorityPort)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "false",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new P0CTestHostEnvironment())
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", P0COwnerSubject)],
                authenticationType: "test")),
            RequestServices = requestServices,
        };
        context.Request.Headers.Authorization = $"Bearer {P0CRawBearer}";
        context.Response.Body = new MemoryStream();
        var textInteraction = new P0CEndpointInteractionService<NyxIdChatCommand>();
        var actionInteraction =
            new P0CEndpointInteractionService<NyxIdActionContinuationCommand>();
        var request = new NyxIdChatEndpoints.NyxIdChatStreamRequest(
            Prompt: null,
            ClientRequestId: "client-key-alpha",
            Type: "action.continue",
            OriginTurnId: P0COriginTurnId,
            Actions:
            [
                new NyxIdChatEndpoints.NyxIdChatActionReportDto(
                    P0CActionRequestId,
                    P0COriginTurnId,
                    "completed",
                    new NyxIdChatEndpoints.NyxIdChatActionResourceDto(
                        Key: new NyxIdChatEndpoints.NyxIdChatKeyRefDto(P0CKeyId))),
            ]);
        var method = typeof(NyxIdChatEndpoints).GetMethod(
            "HandleStreamMessageAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "NyxIdChatEndpoints.HandleStreamMessageAsync was not found.");

        var invocation = method.Invoke(
            null,
            [
                context,
                P0CScopeId,
                P0CActorId,
                request,
                new P0CAllowingScopeAdmissionPort(),
                textInteraction,
                actionInteraction,
                NullLoggerFactory.Instance,
                CancellationToken.None,
            ]);
        await (invocation as Task ?? throw new InvalidOperationException(
            "NyxIdChatEndpoints.HandleStreamMessageAsync did not return a Task."));

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        textInteraction.Commands.Should().BeEmpty();
        var command = actionInteraction.Commands.Should().ContainSingle().Which;
        command.ReadAuthority.Should().NotBeNull();
        command.ReadAuthority!.SecretRef.Should().NotContain(P0CRawBearer);
        command.OwnerSubject.Should().Be(P0COwnerSubject);
        command.ScopeId.Should().Be(P0CScopeId);
        command.OriginTurnId.Should().Be(P0COriginTurnId);
        var envelope = new NyxIdActionContinuationCommandEnvelopeFactory().CreateEnvelope(
            command,
            new CommandContext(
                command.ActorId,
                command.CommandId!,
                command.CorrelationId!,
                new Dictionary<string, string>()));
        var actorCommand = envelope.Payload.Unpack<NyxIdChatActionContinueCommand>();
        actorCommand.ReadAuthority.Should().BeEquivalentTo(command.ReadAuthority);
        return new P0CEndpointContinuation(command, actorCommand, envelope);
    }

    private static async Task P0CPersistBlockedKeyCreateAsync(
        InMemoryEventStoreForTests eventStore,
        DateTimeOffset now)
    {
        var blocked = CreateBlockedActionState();
        var timestamp = Timestamp.FromDateTimeOffset(now);
        blocked.UpdatedAt = timestamp.Clone();
        blocked.ActiveTurn.CreatedAt = timestamp.Clone();
        blocked.LatestTurn.CreatedAt = timestamp.Clone();
        blocked.ActiveTask.CreatedAt = timestamp.Clone();
        blocked.ActiveTask.UpdatedAt = timestamp.Clone();
        blocked.ActiveTask.Gate.Status = NyxIdChatPlanGateStatus.Satisfied;
        blocked.ActiveTask.Gate.DecidedAt = timestamp.Clone();
        var pending = blocked.PendingActions.Single();
        pending.SchemaVersion = NyxIdAssistantActionRegistry.SupportedSchemaVersion;
        pending.RegistryRevision = NyxIdAssistantActionRegistry.LeastScopeRegistryRevision;
        pending.ActionRequestId = P0CActionRequestId;
        pending.Action = NyxIdAssistantActionKind.KeyCreate;
        pending.RequestedAt = Timestamp.FromDateTimeOffset(now.AddMinutes(-2));
        pending.RememberEligible = false;
        pending.Params = new NyxIdAssistantActionParams
        {
            KeyCreate = new NyxIdKeyCreateParams
            {
                Name = "agent-alpha",
                Platform = "codex",
                AllowedServiceIds = { "service-alpha" },
            },
        };
        var browserStep = blocked.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.BrowserAction);
        browserStep.ActionRequestId = P0CActionRequestId;
        browserStep.Source.BrowserAction.ActionRequestId = P0CActionRequestId;
        browserStep.Source.BrowserAction.Action = NyxIdAssistantActionKind.KeyCreate;
        var postconditionStep = blocked.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition);
        postconditionStep.ActionRequestId = P0CActionRequestId;
        postconditionStep.Source.Postcondition.ActionRequestId = P0CActionRequestId;
        postconditionStep.Source.Postcondition.Check = NyxIdAssistantActionKind.KeyCreate.ToString();
        blocked.ActiveTask.Gate = NyxIdChatPlanGateDecisions.BuildActionGate(blocked, pending);
        blocked.ActiveTask.Gate.Status = NyxIdChatPlanGateStatus.Satisfied;
        blocked.ActiveTask.Gate.DecidedAt = timestamp.Clone();
        await PersistActionStateAsync(eventStore, P0CActorId, blocked);
    }

    private static async Task<NyxIdChatTurnOperationExecution> P0CExecutePostconditionAsync(
        NyxIdChatOperationDispatchCommand command,
        INyxIdActionReadAuthorityPort authorityPort,
        TimeProvider clock,
        P0CActionEvidenceReadPort evidence)
    {
        var executor = new NyxIdChatTurnOperationExecutor(
            new P0CUnusedGenerationExecutor(),
            new NyxIdActionPostconditionPort(null, evidence, clock),
            null,
            new NyxIdChatDelegationCredentialLifecyclePort(clock),
            new NyxIdChatToolVerificationPort(),
            authorityPort,
            NullLogger<NyxIdChatTurnOperationExecutor>.Instance);
        var session = new NyxIdChatTransientExecutionSession();

        var execution = await executor.ExecuteAsync(
            command,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        session.Request.Should().BeNull(
            "action-only execution must not depend on a historical LLM session");
        return execution;
    }

    private static NyxIdActionReadAuthorityPort P0CCreateAuthorityPort(
        InMemorySecretVault vault,
        TimeProvider clock) =>
        new(vault, clock, TimeSpan.FromMinutes(10), TimeSpan.FromHours(24));

    private static P0CActionEvidenceReadPort P0CCreateEvidence(DateTimeOffset now) =>
        new(new NyxIdAgentApiKeyEvidence(
            P0CKeyId,
            "agent-alpha",
            ["proxy"],
            "codex",
            true,
            ["service-alpha"],
            false,
            [],
            false,
            now.AddMinutes(-1),
            null));

    private static P0CRecordingCommittedStatePublisher P0CAttachCommittedPublisher(
        NyxIdChatConversationGAgent actor)
    {
        var publisherType = typeof(GAgentBase).Assembly.GetType(
            "Aevatar.Foundation.Core.EventSourcing.ICommittedStateEventPublisher",
            throwOnError: true)!;
        var publisher = (P0CRecordingCommittedStatePublisher)DispatchProxy.Create(
            publisherType,
            typeof(P0CRecordingCommittedStatePublisher));
        var property = typeof(GAgentBase).GetProperty(
            "CommittedStateEventPublisher",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "GAgentBase.CommittedStateEventPublisher was not found.");
        property.SetValue(actor, publisher);
        return publisher;
    }

    private static EventEnvelope P0CWrapActualObservation(
        string actorId,
        CommittedStateEventPublished observation) =>
        new()
        {
            Id = observation.StateEvent.EventId,
            Timestamp = observation.StateEvent.Timestamp?.Clone() ??
                        Timestamp.FromDateTimeOffset(P0CNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(actorId),
            Payload = Any.Pack(observation),
        };

    private static void P0CAssertSerializedBoundary(
        IMessage message,
        string sentinel,
        bool absent)
    {
        if (absent)
            P0CContainsSerialized(message, sentinel).Should().BeFalse();
        else
            P0CContainsSerialized(message, sentinel).Should().BeTrue();
    }

    private static bool P0CContainsSerialized(IMessage message, string sentinel) =>
        Encoding.UTF8.GetString(message.ToByteArray())
            .Contains(sentinel, StringComparison.Ordinal);

    private sealed record P0CEndpointContinuation(
        NyxIdActionContinuationCommand Command,
        NyxIdChatActionContinueCommand ActorCommand,
        EventEnvelope Envelope);

    private sealed class P0CEndpointInteractionService<TCommand>
        : ICommandInteractionService<
            TCommand,
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            AGUIEvent,
            NyxIdChatCompletionStatus>
    {
        public List<TCommand> Commands { get; } = [];

        public async Task<CommandInteractionResult<
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            NyxIdChatCompletionStatus>> ExecuteAsync(
            TCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<NyxIdChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (command is not NyxIdActionContinuationCommand action)
                throw new InvalidOperationException("The text interaction must not be invoked.");

            var receipt = new NyxIdChatAcceptedReceipt(
                action.ActorId,
                action.CommandId!,
                action.CorrelationId!,
                action.ContinuationTurnId,
                action.ScopeId);
            if (onAcceptedAsync is not null)
                await onAcceptedAsync(receipt, ct);
            await emitAsync(new AGUIEvent { RunFinished = new RunFinishedEvent() }, ct);
            return CommandInteractionResult<
                    NyxIdChatAcceptedReceipt,
                    NyxIdChatStartError,
                    NyxIdChatCompletionStatus>
                .Success(
                    receipt,
                    new CommandInteractionFinalizeResult<NyxIdChatCompletionStatus>(
                        NyxIdChatCompletionStatus.Completed,
                        true));
        }

        async Task<RealtimeSessionResult<
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            NyxIdChatCompletionStatus>> IRealtimeSession<
            TCommand,
            NyxIdChatAcceptedReceipt,
            NyxIdChatStartError,
            AGUIEvent,
            NyxIdChatCompletionStatus>.ExecuteAsync(
            TCommand inbound,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<NyxIdChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
            CancellationToken ct) =>
            await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
    }

    private sealed class P0CAllowingScopeAdmissionPort : IScopeResourceAdmissionPort
    {
        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ScopeResourceAdmissionResult.Allowed());
        }
    }

    private sealed class P0CTestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.AI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class P0CActionEvidenceReadPort(NyxIdAgentApiKeyEvidence evidence)
        : INyxIdActionEvidenceReadPort
    {
        public List<string> BearerTokens { get; } = [];
        public List<string> KeyIds { get; } = [];

        public Task<NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>>
            GetUserServiceAuthorizationAsync(
                string bearerToken,
                string userServiceId,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>> GetAgentApiKeyAsync(
            string bearerToken,
            string keyId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BearerTokens.Add(bearerToken);
            KeyIds.Add(keyId);
            return Task.FromResult(new NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>(
                evidence,
                null));
        }
    }

    private sealed class P0CUnusedGenerationExecutor
        : IAgentRunReplyGenerationExecutorPort
    {
        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private class P0CRecordingCommittedStatePublisher : DispatchProxy
    {
        public List<CommittedStateEventPublished> Publications { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is { Length: > 0 } && args[0] is CommittedStateEventPublished evt)
                Publications.Add(evt.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class P0CProjectionWriteDispatcher
        : IProjectionWriteDispatcher<NyxIdChatConversationCurrentStateDocument>
    {
        public List<NyxIdChatConversationCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            NyxIdChatConversationCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class P0CProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
