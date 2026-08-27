using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

[GAgent(NyxIdChatServiceDefaults.GAgentKind)]
public sealed class NyxIdChatConversationGAgent
    : GAgentBase<NyxIdChatConversationGAgentState>
{
    private const string SharedInputHistoryText = "Shared input content.";
    private static readonly TimeSpan ActivationRecoveryDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan HistoryInitializationRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingFirstTurnRetention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PendingSteeringContinuationRetention =
        TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PendingSteeringContinuationRetryDelay =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryReservationRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryTerminalRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OperationDeliveryProbeRetryDelay =
        TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan OperationStepChangedCadence = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan OperationStallThreshold = TimeSpan.FromSeconds(120);
    private const string PostconditionResultRejectedCode =
        "NYXID_CHAT_POSTCONDITION_RESULT_REJECTED";
    private const string PostconditionResultRejectedMessage =
        "The NyxID action postcondition result was rejected.";
    private const string FencedPostconditionResultConsumedCode =
        "NYXID_CHAT_POSTCONDITION_RESULT_CONSUMED_AFTER_CONTROL_FENCE";
    private const string FencedPostconditionResultConsumedMessage =
        "The NyxID postcondition result arrived after the task was stopped.";

    public static string ProjectionKind => "nyxid-chat-conversation";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly AgentTurnToolCatalogMaterializer? _turnCatalogMaterializer;
    private readonly INyxIdChatTurnIntentClassifier? _turnIntentClassifier;

    public NyxIdChatConversationGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider)
        : this(actorRuntime, actorDispatchPort, timeProvider, null, null)
    {
    }

    public NyxIdChatConversationGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer)
        : this(actorRuntime, actorDispatchPort, timeProvider, turnCatalogMaterializer, null)
    {
    }

    public NyxIdChatConversationGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider,
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer,
        INyxIdChatTurnIntentClassifier? turnIntentClassifier = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _turnCatalogMaterializer = turnCatalogMaterializer;
        _turnIntentClassifier = turnIntentClassifier;
    }

    protected override NyxIdChatConversationGAgentState TransitionState(
        NyxIdChatConversationGAgentState current,
        IMessage evt)
    {
        var next = StateTransitionMatcher
            .Match(current, evt)
            .On<AgentProfileBoundEvent>(ApplyAgentProfileBound)
            .On<ConversationContextAttachmentsBoundEvent>(ApplyContextAttachmentsBound)
            .On<NyxIdChatConversationCreationStartedEvent>(ApplyConversationCreationStarted)
            .On<NyxIdChatConversationRegistrationAcceptedEvent>(ApplyConversationRegistrationAccepted)
            .On<NyxIdChatPendingCreationFirstTurnFinalizedEvent>(
                ApplyPendingCreationFirstTurnFinalized)
            .On<NyxIdChatPendingSteeringContinuationFinalizedEvent>(
                ApplyPendingSteeringContinuationFinalized)
            .On<NyxIdChatHistoryInitializationDispatchedEvent>(ApplyHistoryInitializationDispatched)
            .On<NyxIdChatHistoryInitializationRetryScheduledEvent>(ApplyHistoryInitializationRetryScheduled)
            .On<NyxIdChatTurnStartedEvent>(ApplyTurnStarted)
            .On<NyxIdChatHistoryDeliveryReservationDispatchedEvent>(
                ApplyHistoryDeliveryReservationDispatched)
            .On<NyxIdChatHistoryDeliveryReservationRetryScheduledEvent>(
                ApplyHistoryDeliveryReservationRetryScheduled)
            .On<NyxIdChatHistoryTerminalDispatchedEvent>(ApplyHistoryTerminalDispatched)
            .On<NyxIdChatHistoryTerminalRetryScheduledEvent>(ApplyHistoryTerminalRetryScheduled)
            .On<NyxIdChatOperationDispatchedEvent>(ApplyOperationDispatched)
            .On<NyxIdChatOperationDispatchUncertainEvent>(ApplyOperationDispatchUncertain)
            .On<NyxIdChatOperationProgressedEvent>(ApplyOperationProgressed)
            .On<NyxIdChatOperationStepChangedCommittedEvent>(ApplyOperationStepChangedCommitted)
            .On<NyxIdChatOperationStalledEvent>(ApplyOperationStalled)
            .On<NyxIdChatOperationReconciledEvent>(ApplyOperationReconciled)
            .On<NyxIdChatLateOperationEvidenceCommittedEvent>(ApplyLateOperationEvidenceCommitted)
            .On<NyxIdChatControlFenceCommittedEvent>(ApplyControlFenceCommitted)
            .On<NyxIdChatContinuationAdmissionCommittedEvent>(ApplyContinuationAdmissionCommitted)
            .On<NyxIdChatStepControlCommittedEvent>(ApplyStepControlCommitted)
            .On<NyxIdChatActionRequestedEvent>(ApplyActionRequested)
            .On<NyxIdChatInputRequestedEvent>(ApplyInputRequested)
            .On<NyxIdChatInputResolutionCommittedEvent>(ApplyInputResolutionCommitted)
            .On<NyxIdChatApprovalResolutionCommittedEvent>(ApplyApprovalResolutionCommitted)
            .On<NyxIdChatCanaryEffectFaultArmedCommittedEvent>(
                ApplyCanaryEffectFaultArmedCommitted)
            .On<NyxIdChatCanaryEffectFaultConsumedCommittedEvent>(
                ApplyCanaryEffectFaultConsumedCommitted)
            .On<NyxIdChatConversationHistoryDeletedEvent>(ApplyConversationHistoryDeleted)
            .OrCurrent();
        return NyxIdChatNeedsYouDecisions.RefreshAttention(next);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);

        if (State.PendingInputRequest is not null && State.PendingInput is null)
            await DispatchInputRequestContinuationAsync(State.PendingInputRequest, ct);

        if (State.PendingHistoryInitialization is { } pendingInitialization)
        {
            await ScheduleActivationHistoryInitializationAsync(pendingInitialization, ct);
        }
        else if (State.PendingCreationFirstTurn is not null &&
                 !string.IsNullOrWhiteSpace(State.PendingCreationFirstTurnId))
        {
            await SchedulePendingCreationFirstTurnAsync(ActivationRecoveryDelay, ct);
        }

        var hasPendingHistoryReservation = false;
        if (State.HistoryDeliveryReservation is { Dispatched: false } pendingReservation)
        {
            hasPendingHistoryReservation = true;
            await ScheduleActivationHistoryReservationAsync(pendingReservation, ct);
        }

        if (State.PendingHistoryTerminal is { } pendingTerminal &&
            State.HistoryDeliveryReservation?.Dispatched == true)
        {
            await ScheduleActivationHistoryTerminalAsync(pendingTerminal, ct);
        }

        if (IsStartedPendingSteeringContinuation(State))
        {
            await FinalizePendingSteeringContinuationAsync(
                State.PendingSteeringContinuation.Clone(),
                NyxIdChatPendingSteeringContinuationOutcome.Started,
                string.Empty,
                string.Empty);
        }
        else if (CanDispatchPendingSteeringContinuation(State))
            await SchedulePendingSteeringContinuationAsync(ActivationRecoveryDelay, ct);

        if (State.PendingSteeringContinuation is not null)
            await SchedulePendingSteeringContinuationExpiryAsync(ct);

        if (State.PendingApproval is not null)
            await ScheduleToolApprovalExpiryAsync(ct);

        if (State.PendingOperationDeliveryProbe is not null)
            await ScheduleOperationDeliveryProbeAsync(ActivationRecoveryDelay, ct);

        if (hasPendingHistoryReservation || HasPendingOperationRecoveryBarrier(State))
        {
            return;
        }

        await ScheduleOutstandingOperationRecoveryAsync(ct);
        await ScheduleOutstandingOperationStepChangedAsync(ct);
        await ScheduleOutstandingOperationStallCheckAsync(ct);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleCreateConversationAsync(
        NyxIdChatConversationCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scopeId = NormalizeRequired(command.ScopeId, nameof(command.ScopeId));
        var ownerSubject = command.FirstTurn is null
            ? null
            : NormalizeRequired(
                command.FirstTurn.ToolContext?.Caller?.OwnerSubject,
                "owner_subject");
        if (command.FirstTurn is not null &&
            !string.IsNullOrWhiteSpace(State.ConversationActorId) &&
            !OwnerMatches(State.OwnerSubject, ownerSubject))
        {
            await PersistTurnAdmissionRejectionAsync(
                command.FirstTurn,
                "NYXID_CHAT_OWNER_MISMATCH",
                "The chat turn owner does not match the conversation owner.");
            return;
        }
        if (command.FirstTurn is not null &&
            State.ContextAttachments is not null &&
            !ConversationContextAttachmentAdmission.HasAttachments(command.ContextAttachments))
        {
            throw new InvalidOperationException("A conversation cannot remove its context attachments.");
        }
        if (command.FirstTurn is not null && State.ContextAttachments is not null)
            await BindContextAttachmentsAsync(command.ContextAttachments);
        if (command.FirstTurn is not null &&
            string.Equals(State.ScopeId, scopeId, StringComparison.Ordinal) &&
            string.Equals(State.ConversationActorId, Id, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(State.HistoryInitializationOperationId))
        {
            if (State.PendingHistoryInitialization is { } retryInitialization)
            {
                try
                {
                    await DispatchHistoryInitializationContinuationAsync(
                        retryInitialization,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(
                        exception,
                        "NyxIdChat history initialization retry was not admitted: actor={ActorId} operation={OperationId}",
                        Id,
                        retryInitialization.OperationId);
                }

                return;
            }

            await DispatchPendingCreationFirstTurnContinuationAsync(CancellationToken.None);
            return;
        }
        var commandId = ActiveInboundEnvelope?.Id ?? string.Empty;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ?? commandId;

        await BindAgentProfileAsync(command.AgentProfile);
        await BindContextAttachmentsAsync(command.ContextAttachments);
        await PersistDomainEventAsync(new NyxIdChatConversationCreationStartedEvent
        {
            ScopeId = scopeId,
            ActorId = Id,
            CreatedLocally = command.CreatedLocally,
            CommandId = commandId,
            CorrelationId = correlationId,
            OwnerSubject = ownerSubject ?? string.Empty,
        }, CancellationToken.None);

        DurableCallerCredentialRef? pendingFirstTurnCredential = null;
        try
        {
            var receipt = await Services.GetRequiredService<IGAgentActorRegistryCommandPort>()
                .RegisterActorAsync(
                    new GAgentActorRegistration(scopeId, NyxIdChatServiceDefaults.GAgentKind, Id),
                    CancellationToken.None);
            if (receipt.IsAdmissionVisible)
            {
                var next = PrepareHistoryInitializationState(scopeId);
                if (command.FirstTurn is not null)
                {
                    pendingFirstTurnCredential =
                        await StorePendingCreationFirstTurnAsync(command.FirstTurn);
                    next.PendingCreationFirstTurn = pendingFirstTurnCredential.Clone();
                    next.PendingCreationFirstTurnId = command.FirstTurn.TurnId.Trim();
                }
                await PersistDomainEventAsync(new NyxIdChatConversationRegistrationAcceptedEvent
                {
                    ScopeId = scopeId,
                    ActorId = Id,
                    CommandId = commandId,
                    CorrelationId = correlationId,
                    State = next,
                }, CancellationToken.None);
            }
            else
            {
                await PersistRegistrationUnavailableAndCompensateAsync(
                        scopeId,
                        command.CreatedLocally,
                        "registration_not_admission_visible",
                        commandId,
                        correlationId);
                return;
            }
        }
        catch (Exception exception)
        {
            if (pendingFirstTurnCredential is not null &&
                !string.Equals(
                    State.PendingCreationFirstTurn?.Ref,
                    pendingFirstTurnCredential.Ref,
                    StringComparison.Ordinal))
            {
                await RevokePendingFirstTurnCredentialAsync(
                    pendingFirstTurnCredential,
                    "nyxid chat conversation registration did not commit");
            }
            Logger.LogWarning(
                exception,
                "NyxIdChat conversation registration failed: scope={ScopeId} actor={ActorId}",
                scopeId,
                Id);
            await PersistRegistrationUnavailableAndCompensateAsync(
                    scopeId,
                    command.CreatedLocally,
                    "registration_failed",
                    commandId,
                    correlationId);
            return;
        }

        if (State.PendingHistoryInitialization is { } pendingInitialization)
        {
            try
            {
                await DispatchHistoryInitializationContinuationAsync(
                    pendingInitialization,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.LogWarning(
                    exception,
                    "NyxIdChat history initialization could not be admitted before first turn: actor={ActorId} operation={OperationId}",
                    Id,
                    pendingInitialization.OperationId);
            }

            return;
        }

        await DispatchPendingCreationFirstTurnContinuationAsync(CancellationToken.None);
    }

    private async Task RevokePendingFirstTurnCredentialAsync(
        DurableCallerCredentialRef credential,
        string reason)
    {
        var vault = Services.GetService<ISecretVault>();
        if (vault is null)
            return;
        try
        {
            await vault.RevokeAsync(new RevokeSecretRequest(
                credential.Ref,
                CredentialSecretPurposes.NyxIdChatPendingFirstTurn,
                credential.OwnerScopeKey,
                credential.SubjectId,
                reason), CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat pending first-turn orphan cleanup failed: actor={ActorId} ref={CredentialRef}",
                Id,
                credential.Ref);
        }
    }

    private async Task DispatchHistoryInitializationOnceAsync(
        NyxIdChatHistoryInitializationOutbox pending)
    {
        await Services.GetRequiredService<IChatHistoryCommandPort>()
            .InitializeConversationAsync(
                new ChatHistoryConversationInitialization(
                    pending.OperationId,
                    pending.ScopeId,
                    pending.ConversationId,
                    pending.ServiceId,
                    pending.ServiceKind,
                    pending.CreatedAt.ToDateTimeOffset(),
                    NormalizeOptional(pending.InitialTitle)),
                CancellationToken.None);

        await PersistDomainEventAsync(new NyxIdChatHistoryInitializationDispatchedEvent
        {
            OperationId = pending.OperationId,
            Attempt = pending.Attempt,
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
    }

    private async Task<DurableCallerCredentialRef> StorePendingCreationFirstTurnAsync(
        NyxIdChatStartTurnCommand command)
    {
        var ownerSubject = NormalizeRequired(
            command.ToolContext?.Caller?.OwnerSubject,
            "owner_subject");
        var vault = Services.GetService<ISecretVault>() ??
                    throw new InvalidOperationException(
                        "The pending first-turn secret vault is unavailable.");
        var ownerScopeKey = $"nyxid-chat:{Id}";
        var requestedRef = BuildStableIdentity(
            "pending-first-turn",
            Id,
            command.TurnId,
            command.CommandId);
        var stored = await vault.PutAsync(
            new StoreSecretRequest(
                CredentialSecretPurposes.NyxIdChatPendingFirstTurn,
                ownerScopeKey,
                ownerSubject,
                Convert.ToBase64String(command.ToByteArray()),
                "nyxid chat pending first turn",
                _timeProvider.GetUtcNow() + PendingFirstTurnRetention,
                requestedRef),
            CancellationToken.None);
        return new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = CredentialSecretPurposes.NyxIdChatPendingFirstTurn,
            OwnerScopeKey = ownerScopeKey,
            SubjectId = ownerSubject,
            SourceKind = DurableCallerCredentialSourceKind.NyxIdChat,
        };
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandlePendingCreationFirstTurnDispatchRequestedAsync(
        NyxIdChatPendingCreationFirstTurnDispatchRequested signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var pending = State.PendingCreationFirstTurn?.Clone();
        if (pending is null ||
            State.ActiveTurn is not null ||
            !string.Equals(
                pending.Ref,
                signal.CredentialRef,
                StringComparison.Ordinal) ||
            !string.Equals(
                State.PendingCreationFirstTurnId,
                signal.TurnId,
                StringComparison.Ordinal))
        {
            return;
        }

        var vault = Services.GetService<ISecretVault>();
        if (vault is null)
        {
            await FinalizePendingCreationFirstTurnAsync(
                pending,
                signal.TurnId,
                NyxIdChatPendingCreationFirstTurnOutcome.Unavailable,
                "NYXID_CHAT_PENDING_FIRST_TURN_VAULT_UNAVAILABLE",
                "The pending first turn can no longer be resumed.");
            return;
        }
        var resolved = await vault.ResolveAsync(
            new ResolveSecretRequest(
                pending.Ref,
                CredentialSecretPurposes.NyxIdChatPendingFirstTurn,
                pending.OwnerScopeKey,
                pending.SubjectId,
                "nyxid chat resume pending first turn"),
            CancellationToken.None);
        if (!resolved.Resolved)
        {
            await FinalizePendingCreationFirstTurnAsync(
                pending,
                signal.TurnId,
                NyxIdChatPendingCreationFirstTurnOutcome.Unavailable,
                "NYXID_CHAT_PENDING_FIRST_TURN_UNAVAILABLE",
                "The pending first turn can no longer be resumed.");
            return;
        }

        NyxIdChatStartTurnCommand command;
        try
        {
            command = NyxIdChatStartTurnCommand.Parser.ParseFrom(
                Convert.FromBase64String(resolved.Secret ?? string.Empty));
        }
        catch (Exception exception) when (exception is FormatException or InvalidProtocolBufferException)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat pending first-turn command is invalid: actor={ActorId} turn={TurnId}",
                Id,
                signal.TurnId);
            await FinalizePendingCreationFirstTurnAsync(
                pending,
                signal.TurnId,
                NyxIdChatPendingCreationFirstTurnOutcome.Unavailable,
                "NYXID_CHAT_PENDING_FIRST_TURN_INVALID",
                "The pending first turn can no longer be resumed.");
            return;
        }

        if (!string.Equals(command.TurnId, State.PendingCreationFirstTurnId, StringComparison.Ordinal))
        {
            await FinalizePendingCreationFirstTurnAsync(
                pending,
                signal.TurnId,
                NyxIdChatPendingCreationFirstTurnOutcome.Unavailable,
                "NYXID_CHAT_PENDING_FIRST_TURN_IDENTITY_MISMATCH",
                "The pending first turn can no longer be resumed.");
            return;
        }

        await StartTurnCoreAsync(command);
        if (State.ActiveTurn is null ||
            !string.Equals(State.ActiveTurn.TurnId, command.TurnId, StringComparison.Ordinal))
        {
            return;
        }

        await FinalizePendingCreationFirstTurnAsync(
            pending,
            command.TurnId,
            NyxIdChatPendingCreationFirstTurnOutcome.Started,
            string.Empty,
            string.Empty);

        await RevokePendingFirstTurnCredentialAsync(
            pending,
            "nyxid chat first turn finalized");
    }

    private async Task FinalizePendingCreationFirstTurnAsync(
        DurableCallerCredentialRef pending,
        string turnId,
        NyxIdChatPendingCreationFirstTurnOutcome outcome,
        string failureCode,
        string safeMessage)
    {
        await PersistDomainEventAsync(new NyxIdChatPendingCreationFirstTurnFinalizedEvent
        {
            ConversationActorId = Id,
            TurnId = turnId,
            CredentialRef = pending.Ref,
            Outcome = outcome,
            FailureCode = failureCode,
            SafeMessage = safeMessage,
            CommittedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);

        if (outcome == NyxIdChatPendingCreationFirstTurnOutcome.Unavailable)
        {
            await RevokePendingFirstTurnCredentialAsync(
                pending,
                "nyxid chat pending first turn unavailable");
        }
    }

    private Task DispatchPendingCreationFirstTurnContinuationAsync(CancellationToken ct)
    {
        var pending = State.PendingCreationFirstTurn;
        return pending is null || string.IsNullOrWhiteSpace(State.PendingCreationFirstTurnId)
            ? Task.CompletedTask
            : PublishAsync(
                new NyxIdChatPendingCreationFirstTurnDispatchRequested
                {
                    TurnId = State.PendingCreationFirstTurnId,
                    CredentialRef = pending.Ref,
                },
                TopologyAudience.Self,
                ct,
                new EventEnvelopePublishOptions
                {
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        OperationId = BuildStableIdentity(
                            "pending-first-turn-dispatch",
                            Id,
                            State.PendingCreationFirstTurnId,
                            pending.Ref),
                    },
                });
    }

    private Task SchedulePendingCreationFirstTurnAsync(TimeSpan delay, CancellationToken ct)
    {
        var pending = State.PendingCreationFirstTurn;
        return pending is null || string.IsNullOrWhiteSpace(State.PendingCreationFirstTurnId)
            ? Task.CompletedTask
            : ScheduleSelfDurableTimeoutAsync(
                BuildStableIdentity(
                    "pending-first-turn-activation",
                    Id,
                    State.PendingCreationFirstTurnId,
                    pending.Ref),
                delay,
                new NyxIdChatPendingCreationFirstTurnDispatchRequested
                {
                    TurnId = State.PendingCreationFirstTurnId,
                    CredentialRef = pending.Ref,
                },
                ct: ct);
    }

    private async Task<DurableCallerCredentialRef> StorePendingSteeringContinuationAsync(
        NyxIdChatStartTurnCommand command,
        DateTimeOffset expiresAt)
    {
        var vault = Services.GetService<ISecretVault>() ??
                    throw new InvalidOperationException(
                        "The pending steering continuation secret vault is unavailable.");
        var ownerScopeKey = $"nyxid-chat:{Id}";
        var ownerSubject = NormalizeRequired(State.OwnerSubject, "owner_subject");
        var requestedRef = BuildStableIdentity(
            "pending-steering-continuation",
            Id,
            command.TurnId,
            command.CommandId);
        var stored = await vault.PutAsync(
            new StoreSecretRequest(
                CredentialSecretPurposes.NyxIdChatPendingSteeringContinuation,
                ownerScopeKey,
                ownerSubject,
                Convert.ToBase64String(command.ToByteArray()),
                "nyxid chat pending steering continuation",
                expiresAt,
                requestedRef),
            CancellationToken.None);
        return new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = CredentialSecretPurposes.NyxIdChatPendingSteeringContinuation,
            OwnerScopeKey = ownerScopeKey,
            SubjectId = ownerSubject,
            SourceKind = DurableCallerCredentialSourceKind.NyxIdChat,
        };
    }

    private async Task RevokePendingSteeringContinuationAsync(
        DurableCallerCredentialRef pending,
        string reason)
    {
        var vault = Services.GetService<ISecretVault>();
        if (vault is null)
            return;
        try
        {
            await vault.RevokeAsync(new RevokeSecretRequest(
                pending.Ref,
                CredentialSecretPurposes.NyxIdChatPendingSteeringContinuation,
                pending.OwnerScopeKey,
                pending.SubjectId,
                reason), CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat pending steering continuation cleanup failed: actor={ActorId} ref={CredentialRef}",
                Id,
                pending.Ref);
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandlePendingSteeringContinuationDispatchRequestedAsync(
        NyxIdChatPendingSteeringContinuationDispatchRequested signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var pending = State.PendingSteeringContinuation?.Clone();
        var admission = State.ContinuationAdmission?.Clone();
        if (pending is null || admission is not
            {
                Kind: NyxIdChatContinuationKind.Steering,
                Status: NyxIdChatContinuationAdmissionStatus.Accepted,
            })
        {
            return;
        }

        if (!string.Equals(pending.Ref, signal.CredentialRef, StringComparison.Ordinal) ||
            !string.Equals(State.PendingSteeringContinuationId, signal.TurnId,
                StringComparison.Ordinal))
        {
            return;
        }

        var vault = Services.GetService<ISecretVault>();
        if (vault is null)
        {
            Logger.LogWarning(
                "NyxIdChat pending steering continuation vault is temporarily unavailable: actor={ActorId} turn={TurnId}",
                Id,
                signal.TurnId);
            await SchedulePendingSteeringContinuationRetryAsync(CancellationToken.None);
            return;
        }

        ResolveSecretResult resolved;
        try
        {
            resolved = await vault.ResolveAsync(
                new ResolveSecretRequest(
                    pending.Ref,
                    CredentialSecretPurposes.NyxIdChatPendingSteeringContinuation,
                    pending.OwnerScopeKey,
                    pending.SubjectId,
                    "nyxid chat resume pending steering continuation"),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat pending steering continuation vault resolution failed and remains recoverable: actor={ActorId} turn={TurnId}",
                Id,
                signal.TurnId);
            await SchedulePendingSteeringContinuationRetryAsync(CancellationToken.None);
            return;
        }
        if (!resolved.Resolved)
        {
            await FinalizePendingSteeringContinuationAsync(
                pending,
                NyxIdChatPendingSteeringContinuationOutcome.SecretUnavailable,
                "NYXID_CHAT_PENDING_STEERING_CONTINUATION_SECRET_UNAVAILABLE",
                "The delayed steering continuation could not be resumed.");
            return;
        }

        NyxIdChatStartTurnCommand command;
        try
        {
            command = NyxIdChatStartTurnCommand.Parser.ParseFrom(
                Convert.FromBase64String(resolved.Secret ?? string.Empty));
        }
        catch (Exception exception) when (exception is FormatException or InvalidProtocolBufferException)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat pending steering continuation is invalid: actor={ActorId} turn={TurnId}",
                Id,
                signal.TurnId);
            await FinalizePendingSteeringContinuationAsync(
                pending,
                NyxIdChatPendingSteeringContinuationOutcome.InvalidCommand,
                "NYXID_CHAT_PENDING_STEERING_CONTINUATION_INVALID",
                "The delayed steering continuation could not be resumed.");
            return;
        }

        if (!MatchesPendingSteeringContinuation(command, admission))
        {
            await FinalizePendingSteeringContinuationAsync(
                pending,
                NyxIdChatPendingSteeringContinuationOutcome.IdentityMismatch,
                "NYXID_CHAT_PENDING_STEERING_CONTINUATION_IDENTITY_MISMATCH",
                "The delayed steering continuation could not be resumed.");
            return;
        }

        // Refresh only from the actor's latest committed task after the safe
        // checkpoint; the vaulted command owns capability, not business facts.
        command.SteeringExecutionContext = BuildSteeringExecutionContext(admission);
        try
        {
            await StartTurnCoreAsync(command);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat pending steering continuation start failed and remains recoverable: actor={ActorId} turn={TurnId}",
                Id,
                command.TurnId);
            if (State.ActiveTurn is not null &&
                string.Equals(State.ActiveTurn.TurnId, command.TurnId, StringComparison.Ordinal))
            {
                await FinalizePendingSteeringContinuationAsync(
                    pending,
                    NyxIdChatPendingSteeringContinuationOutcome.Started,
                    string.Empty,
                    string.Empty);
                return;
            }

            await SchedulePendingSteeringContinuationRetryAsync(CancellationToken.None);
            return;
        }

        if (State.ActiveTurn is null ||
            !string.Equals(State.ActiveTurn.TurnId, command.TurnId, StringComparison.Ordinal))
            return;

        await FinalizePendingSteeringContinuationAsync(
            pending,
            NyxIdChatPendingSteeringContinuationOutcome.Started,
            string.Empty,
            string.Empty);
    }

    private async Task FinalizePendingSteeringContinuationAsync(
        DurableCallerCredentialRef pending,
        NyxIdChatPendingSteeringContinuationOutcome outcome,
        string failureCode,
        string safeMessage)
    {
        var next = State.Clone();
        var continuationTurnId = next.PendingSteeringContinuationId;
        next.PendingSteeringContinuation = null;
        next.PendingSteeringContinuationId = string.Empty;
        next.PendingSteeringContinuationExpiresAt = null;
        if (next.ContinuationAdmission is
            {
                Kind: NyxIdChatContinuationKind.Steering,
            } admission)
        {
            admission.Status = outcome == NyxIdChatPendingSteeringContinuationOutcome.Started
                ? NyxIdChatContinuationAdmissionStatus.Started
                : NyxIdChatContinuationAdmissionStatus.Rejected;
            if (outcome != NyxIdChatPendingSteeringContinuationOutcome.Started)
            {
                admission.ReasonCode = failureCode;
                admission.SafeMessage = safeMessage;
            }
        }

        next.ProgressSequence = checked(next.ProgressSequence + 1);
        next.UpdatedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        await PersistDomainEventAsync(new NyxIdChatPendingSteeringContinuationFinalizedEvent
        {
            ConversationActorId = Id,
            ContinuationTurnId = continuationTurnId,
            CredentialRef = pending.Ref,
            Outcome = outcome,
            FailureCode = failureCode,
            SafeMessage = safeMessage,
            CommittedAt = next.UpdatedAt.Clone(),
            State = next,
        }, CancellationToken.None);
        await RevokePendingSteeringContinuationAsync(
            pending,
            outcome == NyxIdChatPendingSteeringContinuationOutcome.Started
                ? "nyxid chat steering continuation dispatched"
                : "nyxid chat steering continuation unavailable");
    }

    private Task DispatchPendingSteeringContinuationAsync(CancellationToken ct)
    {
        var pending = State.PendingSteeringContinuation;
        return !CanDispatchPendingSteeringContinuation(State) || pending is null
            ? Task.CompletedTask
            : PublishAsync(
                new NyxIdChatPendingSteeringContinuationDispatchRequested
                {
                    TurnId = State.PendingSteeringContinuationId,
                    CredentialRef = pending.Ref,
                },
                TopologyAudience.Self,
                ct,
                new EventEnvelopePublishOptions
                {
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        OperationId = BuildStableIdentity(
                            "pending-steering-continuation-dispatch",
                            Id,
                            State.PendingSteeringContinuationId,
                            pending.Ref),
                    },
                });
    }

    private Task SchedulePendingSteeringContinuationAsync(TimeSpan delay, CancellationToken ct)
    {
        var pending = State.PendingSteeringContinuation;
        return !CanDispatchPendingSteeringContinuation(State) || pending is null
            ? Task.CompletedTask
            : ScheduleSelfDurableTimeoutAsync(
                BuildStableIdentity(
                    "pending-steering-continuation-activation",
                    Id,
                    State.PendingSteeringContinuationId,
                    pending.Ref),
                delay,
                new NyxIdChatPendingSteeringContinuationDispatchRequested
                {
                    TurnId = State.PendingSteeringContinuationId,
                    CredentialRef = pending.Ref,
                },
                ct: ct);
    }

    private Task SchedulePendingSteeringContinuationRetryAsync(CancellationToken ct)
    {
        var pending = State.PendingSteeringContinuation;
        var expiresAt = State.PendingSteeringContinuationExpiresAt;
        if (!CanDispatchPendingSteeringContinuation(State) ||
            pending is null ||
            expiresAt is null)
        {
            return Task.CompletedTask;
        }

        var now = _timeProvider.GetUtcNow();
        var remaining = expiresAt.ToDateTimeOffset() - now;
        if (remaining <= ActivationRecoveryDelay)
            return Task.CompletedTask;

        var retryWindow = remaining - ActivationRecoveryDelay;
        var delay = retryWindow < PendingSteeringContinuationRetryDelay
            ? retryWindow
            : PendingSteeringContinuationRetryDelay;
        if (delay < ActivationRecoveryDelay)
            delay = ActivationRecoveryDelay;
        var retryAt = now + delay;
        return ScheduleSelfDurableTimeoutAsync(
            BuildStableIdentity(
                "pending-steering-continuation-retry",
                Id,
                State.PendingSteeringContinuationId,
                pending.Ref,
                retryAt.ToUnixTimeMilliseconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            delay,
            new NyxIdChatPendingSteeringContinuationDispatchRequested
            {
                TurnId = State.PendingSteeringContinuationId,
                CredentialRef = pending.Ref,
            },
            ct: ct);
    }

    private Task SchedulePendingSteeringContinuationExpiryAsync(CancellationToken ct)
    {
        var pending = State.PendingSteeringContinuation;
        var expiresAt = State.PendingSteeringContinuationExpiresAt;
        if (pending is null || expiresAt is null ||
            string.IsNullOrWhiteSpace(State.PendingSteeringContinuationId))
        {
            return Task.CompletedTask;
        }

        var delay = expiresAt.ToDateTimeOffset() - _timeProvider.GetUtcNow();
        if (delay < ActivationRecoveryDelay)
            delay = ActivationRecoveryDelay;
        return ScheduleSelfDurableTimeoutAsync(
            BuildStableIdentity(
                "pending-steering-continuation-expiry",
                Id,
                State.PendingSteeringContinuationId,
                pending.Ref),
            delay,
            new NyxIdChatPendingSteeringContinuationExpired
            {
                TurnId = State.PendingSteeringContinuationId,
                CredentialRef = pending.Ref,
                ExpectedExpiresAt = expiresAt.Clone(),
            },
            ct: ct);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandlePendingSteeringContinuationExpiredAsync(
        NyxIdChatPendingSteeringContinuationExpired signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var pending = State.PendingSteeringContinuation?.Clone();
        if (pending is null ||
            !string.Equals(pending.Ref, signal.CredentialRef, StringComparison.Ordinal) ||
            !string.Equals(State.PendingSteeringContinuationId, signal.TurnId,
                StringComparison.Ordinal) ||
            State.PendingSteeringContinuationExpiresAt is null ||
            signal.ExpectedExpiresAt is null ||
            !State.PendingSteeringContinuationExpiresAt.Equals(signal.ExpectedExpiresAt))
        {
            return;
        }

        if (_timeProvider.GetUtcNow() < signal.ExpectedExpiresAt.ToDateTimeOffset())
        {
            await SchedulePendingSteeringContinuationExpiryAsync(CancellationToken.None);
            return;
        }

        await FinalizePendingSteeringContinuationAsync(
            pending,
            NyxIdChatPendingSteeringContinuationOutcome.SecretUnavailable,
            "NYXID_CHAT_PENDING_STEERING_CONTINUATION_EXPIRED",
            "The delayed steering continuation expired before effect verification completed.");
    }

    private static bool CanDispatchPendingSteeringContinuation(
        NyxIdChatConversationGAgentState state) =>
        state.PendingSteeringContinuation is not null &&
        !string.IsNullOrWhiteSpace(state.PendingSteeringContinuationId) &&
        state.ContinuationAdmission is
        {
            Kind: NyxIdChatContinuationKind.Steering,
            Status: NyxIdChatContinuationAdmissionStatus.Accepted,
        };

    private static bool IsStartedPendingSteeringContinuation(
        NyxIdChatConversationGAgentState state) =>
        state.PendingSteeringContinuation is not null &&
        !string.IsNullOrWhiteSpace(state.PendingSteeringContinuationId) &&
        state.ContinuationAdmission?.Status == NyxIdChatContinuationAdmissionStatus.Started &&
        string.Equals(
            state.ActiveTurn?.TurnId,
            state.PendingSteeringContinuationId,
            StringComparison.Ordinal);

    [EventHandler]
    public async Task HandleWorkflowInteractiveActionHandoffAsync(
        WorkflowInteractiveActionHandoffCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateWorkflowInteractiveActionHandoff(command);
        var wireRequest = command.Request;
        var registry = Services.GetRequiredService<NyxIdAssistantActionRegistry>();
        var validated = ValidateWorkflowInteractiveActionRequest(registry, wireRequest);

        if (!string.IsNullOrWhiteSpace(State.ConversationActorId))
        {
            if (!string.Equals(State.ConversationActorId, Id, StringComparison.Ordinal) ||
                !string.Equals(State.ScopeId, command.ScopeId, StringComparison.Ordinal) ||
                !string.Equals(State.OwnerSubject, command.OwnerSubject, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A workflow action handoff cannot replace the conversation authority.");
            }

            var existing = State.PendingActions
                .Concat(State.RecentActions)
                .FirstOrDefault(candidate => string.Equals(
                    candidate.ActionRequestId,
                    wireRequest.ActionRequestId,
                    StringComparison.Ordinal));
            if (existing is null ||
                !WorkflowInteractiveActionMatches(existing, wireRequest, validated))
            {
                throw new InvalidOperationException(
                    "A workflow action handoff identity was reused with different content.");
            }

            return;
        }

        var commandId = ActiveInboundEnvelope?.Id ?? command.HandoffId;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ?? commandId;
        await PersistDomainEventAsync(new NyxIdChatConversationCreationStartedEvent
        {
            ScopeId = command.ScopeId,
            ActorId = Id,
            CreatedLocally = true,
            CommandId = commandId,
            CorrelationId = correlationId,
            OwnerSubject = command.OwnerSubject,
        }, CancellationToken.None);

        var receipt = await Services.GetRequiredService<IGAgentActorRegistryCommandPort>()
            .RegisterActorAsync(
                new GAgentActorRegistration(
                    command.ScopeId,
                    NyxIdChatServiceDefaults.GAgentKind,
                    Id),
                CancellationToken.None);
        if (!receipt.IsAdmissionVisible)
        {
            throw new InvalidOperationException(
                "The workflow action actor registration is not admission visible.");
        }

        await PersistDomainEventAsync(new NyxIdChatConversationRegistrationAcceptedEvent
        {
            ScopeId = command.ScopeId,
            ActorId = Id,
            CommandId = commandId,
            CorrelationId = correlationId,
            State = PrepareHistoryInitializationState(command.ScopeId),
        }, CancellationToken.None);

        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var actionBase = State.Clone();
        actionBase.ActiveTurn = new NyxIdChatTurnState
        {
            TurnId = wireRequest.OriginTurnId,
            TaskId = wireRequest.TaskId,
            ClientRequestId = command.HandoffId,
            CommandId = commandId,
            Status = NyxIdChatTurnStatus.Active,
            CreatedAt = now.Clone(),
        };
        actionBase.LatestTurn = actionBase.ActiveTurn.Clone();
        actionBase.ActiveTask = new NyxIdChatTaskState
        {
            TurnId = wireRequest.OriginTurnId,
            TaskId = wireRequest.TaskId,
            Status = NyxIdChatTaskStatus.Active,
            CreatedAt = now.Clone(),
            UpdatedAt = now.Clone(),
            SchemaVersion = 5,
            ActorId = Id,
            PlanId = wireRequest.TaskId,
            PlanRevision = 1,
            Title = "Complete the requested NyxID action",
        };
        actionBase.ProgressSequence = Math.Max(1, State.ProgressSequence + 1);
        actionBase.UpdatedAt = now.Clone();

        var actionRequest = new NyxIdChatActionRequestState
        {
            SchemaVersion = validated.Definition.SchemaVersion,
            RegistryRevision = validated.Definition.RegistryRevision,
            ConversationActorId = Id,
            OriginTurnId = wireRequest.OriginTurnId,
            TaskId = wireRequest.TaskId,
            StepId = wireRequest.StepId,
            ActionRequestId = wireRequest.ActionRequestId,
            Action = validated.Definition.Action,
            Params = validated.Params.Clone(),
            AdvisoryRisk = validated.Definition.AdvisoryRisk,
            RememberEligible = validated.Definition.RememberEligible,
            RequestedAt = now.Clone(),
        };
        var decision = NyxIdChatBrowserActions.CommitRequest(actionBase, actionRequest, now);
        if (!decision.ShouldCommit || decision.Outcome != NyxIdChatTransitionOutcome.Accepted)
        {
            throw new InvalidOperationException(
                "The workflow action handoff could not establish an action request.");
        }

        var actionState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        await PersistDomainEventAsync(new NyxIdChatActionRequestedEvent
        {
            Request = decision.Request.Clone(),
            Task = actionState.ActiveTask.Clone(),
            OriginTurn = actionState.ActiveTurn.Clone(),
            State = actionState,
        }, CancellationToken.None);
    }

    private void ValidateWorkflowInteractiveActionHandoff(
        WorkflowInteractiveActionHandoffCommand command)
    {
        var request = command.Request;
        var requestParams = request?.Params;
        var hasCatalogServiceConnect =
            string.Equals(request?.Action, "service.connect", StringComparison.Ordinal) &&
            requestParams?.CatalogService is not null &&
            requestParams.KeyCreate is null &&
            !string.IsNullOrWhiteSpace(requestParams.CatalogService.ServiceSlug);
        var hasKeyCreate =
            string.Equals(request?.Action, "key.create", StringComparison.Ordinal) &&
            requestParams?.KeyCreate is not null &&
            requestParams.CatalogService is null;
        if (request is null ||
            !IsValidWorkflowActionActorId(Id) ||
            !string.Equals(request.ActorId, Id, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(command.HandoffId) ||
            string.IsNullOrWhiteSpace(command.ScopeId) ||
            string.IsNullOrWhiteSpace(command.OwnerSubject) ||
            string.IsNullOrWhiteSpace(command.SourceWorkflowActorId) ||
            request.SchemaVersion != NyxIdAssistantActionRegistry.SupportedSchemaVersion ||
            (!hasCatalogServiceConnect && !hasKeyCreate) ||
            string.IsNullOrWhiteSpace(request.OriginTurnId) ||
            string.IsNullOrWhiteSpace(request.TaskId) ||
            string.IsNullOrWhiteSpace(request.StepId) ||
            string.IsNullOrWhiteSpace(request.ActionRequestId))
        {
            throw new InvalidOperationException(
                "The workflow interactive action handoff is invalid.");
        }
    }

    private static NyxIdAssistantActionValidation ValidateWorkflowInteractiveActionRequest(
        NyxIdAssistantActionRegistry registry,
        WorkflowInteractiveActionRequestWirePayload request)
    {
        if (string.Equals(request.Action, "service.connect", StringComparison.Ordinal))
        {
            return registry.ResolveCatalogServiceConnect(
                request.Params.CatalogService.ServiceSlug,
                request.Params.CatalogService.RequestedScopes);
        }

        var keyCreate = request.Params.KeyCreate;
        var paramsJson = JsonSerializer.Serialize(new
        {
            name = keyCreate.Name,
            platform = keyCreate.Platform,
            allowedServiceIds = keyCreate.AllowedServiceIds.ToArray(),
        });
        return registry.ValidateRequest("key.create", paramsJson);
    }

    private static bool IsValidWorkflowActionActorId(string actorId)
    {
        const string prefix = "nyxid-chat-";
        if (!actorId.StartsWith(prefix, StringComparison.Ordinal) ||
            actorId.Length <= prefix.Length ||
            actorId.Length > 128)
        {
            return false;
        }

        return actorId[prefix.Length..].All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool WorkflowInteractiveActionMatches(
        NyxIdChatActionRequestState existing,
        WorkflowInteractiveActionRequestWirePayload wireRequest,
        NyxIdAssistantActionValidation validated) =>
        existing.SchemaVersion == wireRequest.SchemaVersion &&
        string.Equals(existing.RegistryRevision, validated.Definition.RegistryRevision, StringComparison.Ordinal) &&
        string.Equals(existing.ConversationActorId, wireRequest.ActorId, StringComparison.Ordinal) &&
        string.Equals(existing.OriginTurnId, wireRequest.OriginTurnId, StringComparison.Ordinal) &&
        string.Equals(existing.TaskId, wireRequest.TaskId, StringComparison.Ordinal) &&
        string.Equals(existing.StepId, wireRequest.StepId, StringComparison.Ordinal) &&
        string.Equals(existing.ActionRequestId, wireRequest.ActionRequestId, StringComparison.Ordinal) &&
        existing.Action == validated.Definition.Action &&
        existing.Params.ToByteString().Equals(validated.Params.ToByteString());

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleHistoryInitializationDispatchRequestedAsync(
        NyxIdChatHistoryInitializationDispatchRequested signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var pending = State.PendingHistoryInitialization;
        if (pending is null ||
            !string.Equals(pending.OperationId, signal.OperationId, StringComparison.Ordinal) ||
            pending.Attempt != signal.Attempt)
        {
            return;
        }

        try
        {
            await DispatchHistoryInitializationOnceAsync(pending);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat history initialization dispatch failed: actor={ActorId} operation={OperationId} attempt={Attempt}",
                Id,
                pending.OperationId,
                pending.Attempt);

            var nextAttempt = pending.Attempt == int.MaxValue
                ? int.MaxValue
                : Math.Max(1, pending.Attempt + 1);
            await PersistDomainEventAsync(new NyxIdChatHistoryInitializationRetryScheduledEvent
            {
                OperationId = pending.OperationId,
                Attempt = nextAttempt,
                FailureCode = "history_initialization_dispatch_failed",
                ScheduledAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);

            try
            {
                await ScheduleSelfDurableTimeoutAsync(
                        BuildStableIdentity(
                            "history-initialization-retry",
                            pending.OperationId,
                            nextAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        HistoryInitializationRetryDelay,
                        new NyxIdChatHistoryInitializationDispatchRequested
                        {
                            OperationId = pending.OperationId,
                            Attempt = nextAttempt,
                        },
                        ct: CancellationToken.None);
            }
            catch (Exception schedulingException)
            {
                Logger.LogWarning(
                    schedulingException,
                    "NyxIdChat history initialization retry scheduling failed: actor={ActorId} operation={OperationId} attempt={Attempt}",
                    Id,
                    pending.OperationId,
                        nextAttempt);
            }

            return;
        }

        await DispatchPendingCreationFirstTurnContinuationAsync(CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleHistoryDeliveryReservationDispatchRequestedAsync(
        NyxIdChatHistoryDeliveryReservationDispatchRequested signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var pending = State.HistoryDeliveryReservation;
        if (pending is null || pending.Dispatched ||
            !string.Equals(pending.DeliveryId, signal.DeliveryId, StringComparison.Ordinal) ||
            pending.Attempt != signal.Attempt)
        {
            return;
        }

        try
        {
            await ReserveHistoryDeliveryAsync(pending, CancellationToken.None);
            await PersistDomainEventAsync(new NyxIdChatHistoryDeliveryReservationDispatchedEvent
            {
                DeliveryId = pending.DeliveryId,
                SourceCommandId = pending.SourceCommandId,
                DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
            await DispatchPendingHistoryTerminalAsync();
            await ScheduleOutstandingOperationRecoveryAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                "NyxIdChat history reservation retry failed: actor={ActorId} delivery={DeliveryId} attempt={Attempt} exceptionType={ExceptionType}",
                Id,
                pending.DeliveryId,
                pending.Attempt,
                exception.GetType().Name);
            await ScheduleHistoryReservationRetryAsync(pending);
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleHistoryTerminalDispatchRequestedAsync(
        NyxIdChatHistoryTerminalDispatchRequested signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var pending = State.PendingHistoryTerminal;
        if (pending is null ||
            !string.Equals(pending.DeliveryId, signal.DeliveryId, StringComparison.Ordinal) ||
            pending.Attempt != signal.Attempt)
        {
            return;
        }

        try
        {
            await Services.GetRequiredService<IChatHistoryCommandPort>()
                .NotifyTurnTerminalAsync(
                    new ChatHistoryTurnTerminalNotification(
                        pending.DeliveryId,
                        pending.SourceActorId,
                        pending.SourceCommandId,
                        ToHistoryTerminalStatus(pending.Status),
                        pending.Text,
                        pending.ErrorCode,
                        pending.ObservedAt.ToDateTimeOffset(),
                        // Absent rather than empty: a turn that ran no Model or Tool
                        // operation reported no ledger at all.
                        pending.Operations.Count == 0
                            ? null
                            : pending.Operations.Select(ToHistoryTurnOperation).ToList()),
                    CancellationToken.None);

            await PersistDomainEventAsync(new NyxIdChatHistoryTerminalDispatchedEvent
            {
                DeliveryId = pending.DeliveryId,
                SourceCommandId = pending.SourceCommandId,
                Attempt = pending.Attempt,
                DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                "NyxIdChat history terminal dispatch failed: actor={ActorId} delivery={DeliveryId} attempt={Attempt} exceptionType={ExceptionType}",
                Id,
                pending.DeliveryId,
                pending.Attempt,
                exception.GetType().Name);

            var nextAttempt = pending.Attempt == int.MaxValue
                ? int.MaxValue
                : Math.Max(1, pending.Attempt + 1);
            await PersistDomainEventAsync(new NyxIdChatHistoryTerminalRetryScheduledEvent
            {
                DeliveryId = pending.DeliveryId,
                Attempt = nextAttempt,
                FailureCode = "history_terminal_dispatch_failed",
                ScheduledAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);

            try
            {
                await ScheduleSelfDurableTimeoutAsync(
                        BuildStableIdentity(
                            "history-terminal-retry",
                            pending.DeliveryId,
                            nextAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        HistoryTerminalRetryDelay,
                        new NyxIdChatHistoryTerminalDispatchRequested
                        {
                            DeliveryId = pending.DeliveryId,
                            Attempt = nextAttempt,
                        },
                        ct: CancellationToken.None);
            }
            catch (Exception schedulingException)
            {
                Logger.LogWarning(
                    "NyxIdChat history terminal retry scheduling failed: actor={ActorId} delivery={DeliveryId} attempt={Attempt} exceptionType={ExceptionType}",
                    Id,
                    pending.DeliveryId,
                    nextAttempt,
                    schedulingException.GetType().Name);
            }
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleCreationCompensationAsync(
        NyxIdChatConversationCreationCompensationRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await Services.GetRequiredService<IGAgentActorRegistryCommandPort>()
                .UnregisterActorAsync(
                    new GAgentActorRegistration(
                        command.ScopeId,
                        NyxIdChatServiceDefaults.GAgentKind,
                        command.ActorId),
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "Failed to unregister NyxIdChat conversation during compensation: scope={ScopeId} actor={ActorId}",
                command.ScopeId,
                command.ActorId);
            return;
        }

        if (!command.DestroyActor)
            return;

        try
        {
            await _actorRuntime.DestroyAsync(command.ActorId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "Failed to destroy NyxIdChat conversation during compensation: actor={ActorId}",
                command.ActorId);
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleDeleteConversationAsync(
        NyxIdChatConversationDeleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(Id, command.ActorId?.Trim(), StringComparison.Ordinal))
            return;

        var scopeId = NormalizeRequired(command.ScopeId, nameof(command.ScopeId));
        var commandId = ActiveInboundEnvelope?.Id ?? string.Empty;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ?? commandId;
        var registry = Services.GetRequiredService<IGAgentActorRegistryCommandPort>();

        await PersistDomainEventAsync(new NyxIdChatConversationDeletionStartedEvent
        {
            ScopeId = scopeId,
            ActorId = Id,
            CommandId = commandId,
            CorrelationId = correlationId,
        }, CancellationToken.None);
        await registry.UnregisterActorAsync(
                new GAgentActorRegistration(scopeId, NyxIdChatServiceDefaults.GAgentKind, Id),
                CancellationToken.None);
        await PersistDomainEventAsync(new NyxIdChatConversationUnregisteredEvent
        {
            ScopeId = scopeId,
            ActorId = Id,
            CommandId = commandId,
            CorrelationId = correlationId,
        }, CancellationToken.None);

        try
        {
            await Services.GetRequiredService<IChatHistoryCommandPort>()
                .DeleteConversationAsync(scopeId, Id, CancellationToken.None);
            await PersistDomainEventAsync(new NyxIdChatConversationHistoryDeletedEvent
            {
                ScopeId = scopeId,
                ActorId = Id,
                CommandId = commandId,
                CorrelationId = correlationId,
                DeletedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
        }
        catch
        {
            await PersistDomainEventAsync(new NyxIdChatConversationDeletionCompensationStartedEvent
            {
                ScopeId = scopeId,
                ActorId = Id,
                Reason = "history_delete_failed",
                CommandId = commandId,
                CorrelationId = correlationId,
            }, CancellationToken.None);
            await HandleDeletionCompensationAsync(new NyxIdChatConversationDeletionCompensationRequested
            {
                ScopeId = scopeId,
                ActorId = Id,
                Reason = "history_delete_failed",
            });
            throw;
        }
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleDeletionCompensationAsync(
        NyxIdChatConversationDeletionCompensationRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await Services.GetRequiredService<IGAgentActorRegistryCommandPort>()
                .RegisterActorAsync(
                    new GAgentActorRegistration(
                        command.ScopeId,
                        NyxIdChatServiceDefaults.GAgentKind,
                        command.ActorId),
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogError(
                exception,
                "Failed to restore NyxIdChat registration: scope={ScopeId} actor={ActorId}",
                command.ScopeId,
                command.ActorId);
        }
    }

    [EventHandler]
    public Task HandleStartTurnAsync(NyxIdChatStartTurnCommand command) =>
        StartTurnCoreAsync(command);

    private async Task StartTurnCoreAsync(NyxIdChatStartTurnCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateStartCommand(command);
        if (!OwnerMatches(
                State.OwnerSubject,
                NormalizeOptional(command.ToolContext?.Caller?.OwnerSubject)))
        {
            await PersistTurnAdmissionRejectionAsync(
                command,
                "NYXID_CHAT_OWNER_MISMATCH",
                "The chat turn owner does not match the conversation owner.");
            return;
        }

        if (State.ActiveTurn is not null)
        {
            if (SameTurnAdmission(State, command))
                return;

            if (SameTurnIdentity(State, command))
            {
                await PersistTurnAdmissionRejectionAsync(
                    command,
                    "IDEMPOTENCY_CONFLICT",
                    "This client request id was already used for different input.");
                return;
            }

            if (State.ActiveTurn.Status == NyxIdChatTurnStatus.Active)
            {
                await PersistTurnAdmissionRejectionAsync(
                    command,
                    NyxIdChatControlCommands.ActiveTurnRequiresSteering,
                    NyxIdChatControlCommands.ActiveTurnRequiresSteeringMessage);
                return;
            }
        }

        command.SteeringExecutionContext =
            command.AddedBy == NyxIdChatStepAddedBy.Steering &&
            State.ContinuationAdmission is
            {
                Kind: NyxIdChatContinuationKind.Steering,
            } steeringAdmission &&
            string.Equals(
                steeringAdmission.ContinuationTurnId,
                command.TurnId.Trim(),
                StringComparison.Ordinal)
                ? BuildSteeringExecutionContext(steeringAdmission)
                : null;
        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var turnAuthority = await PrepareAgentProfileTurnAuthorityAsync(command);
        var intent = await ClassifyTurnIntentAsync(command, turnAuthority);
        var operationKey = new NyxIdChatOperationKey
        {
            ConversationActorId = Id,
            TurnId = command.TurnId.Trim(),
            TaskId = command.TaskId.Trim(),
            StepId = BuildStableIdentity("step", Id, command.TurnId, command.TaskId, "llm"),
            OperationId = BuildStableIdentity("operation", Id, command.TurnId, command.TaskId, "llm", "1"),
            OperationGeneration = 1,
        };
        var next = NyxIdChatNeedsYouDecisions.RefreshAttention(
            BuildStartedState(command, operationKey, turnAuthority, intent, now));
        next.HistoryDeliveryReservation = BuildHistoryDeliveryReservation(command);

        await PersistDomainEventAsync(new NyxIdChatTurnStartedEvent
        {
            State = next,
        }, CancellationToken.None);

        try
        {
            await ReserveHistoryDeliveryAsync(
                    State.HistoryDeliveryReservation,
                    CancellationToken.None);
            await PersistDomainEventAsync(new NyxIdChatHistoryDeliveryReservationDispatchedEvent
            {
                DeliveryId = State.HistoryDeliveryReservation.DeliveryId,
                SourceCommandId = State.HistoryDeliveryReservation.SourceCommandId,
                DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistOperationDispatchFailureAsync(
                    operationKey,
                    "NYXID_CHAT_HISTORY_RESERVATION_FAILED",
                    "The chat turn could not reserve its transcript delivery.",
                    exception);
            return;
        }

        var dispatchCommand = new NyxIdChatOperationDispatchCommand
        {
            Key = operationKey.Clone(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = BuildTransientChatRequest(command),
                AgentProfile = turnAuthority is null ? null : State.AgentProfile?.Clone(),
                AgentProfileTurnAuthority = turnAuthority?.Clone(),
                Intent = intent,
                ContextAttachments = State.ContextAttachments?.Clone(),
                TargetRef = command.TargetRef?.Clone(),
            },
        };
        await DispatchFirstOperationAsync(
                dispatchCommand,
                command.CorrelationId,
                now);
    }

    [EventHandler]
    public async Task HandleStopAsync(NyxIdChatStopCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationToCancel = ResolvePhysicallyInFlightOperation(State)?.Key?.Clone();
        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var decision = NyxIdChatControlCommands.Stop(
            State,
            command,
            CurrentCommittedVersion(),
            now);
        if (!decision.ShouldCommit)
            return;

        var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        var terminalPrepared = PrepareHistoryTerminalOutbox(nextState);

        await PersistDomainEventAsync(new NyxIdChatControlFenceCommittedEvent
        {
            Fence = decision.Result.Clone(),
            Task = nextState.ActiveTask?.Clone(),
            Turn = nextState.ActiveTurn?.Clone(),
            State = nextState,
        }, CancellationToken.None);

        if (operationToCancel is not null)
            await DispatchOperationCancellationAsync(operationToCancel);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();
    }

    [EventHandler]
    public async Task HandleSteeringAsync(NyxIdChatSteeringCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operationToCancel = ResolvePhysicallyInFlightOperation(State)?.Key?.Clone();
        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var decision = NyxIdChatControlCommands.Steer(
            State,
            command,
            CurrentCommittedVersion(),
            now);
        if (!decision.ShouldCommit)
        {
            if (decision.StartContinuationNow && decision.Admission is not null)
            {
                if (State.PendingSteeringContinuation is not null)
                    await DispatchPendingSteeringContinuationAsync(CancellationToken.None);
                else
                    await DispatchSteeringContinuationAsync(command, decision.Admission);
            }
            return;
        }

        DurableCallerCredentialRef? pendingSteeringContinuation = null;
        Timestamp? pendingSteeringContinuationExpiresAt = null;
        if (decision.Admission?.Status ==
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater)
        {
            NormalizeRequired(State.OwnerSubject, "owner_subject");
            if (!OwnerMatches(
                    State.OwnerSubject,
                    command.ToolContext?.Caller?.OwnerSubject))
            {
                throw new ArgumentException(
                    "The steering continuation owner does not match the conversation owner.",
                    nameof(command));
            }
            var start = BuildSteeringContinuationCommand(command, decision.Admission);
            pendingSteeringContinuationExpiresAt = Timestamp.FromDateTimeOffset(
                _timeProvider.GetUtcNow() + PendingSteeringContinuationRetention);
            pendingSteeringContinuation = await StorePendingSteeringContinuationAsync(
                start,
                pendingSteeringContinuationExpiresAt.ToDateTimeOffset());
        }

        var terminalPrepared = false;
        try
        {
            var fencedState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.FencedState);
            terminalPrepared = PrepareHistoryTerminalOutbox(fencedState);
            await PersistDomainEventAsync(new NyxIdChatControlFenceCommittedEvent
            {
                Fence = decision.Result.Clone(),
                Task = fencedState.ActiveTask?.Clone(),
                Turn = fencedState.ActiveTurn?.Clone(),
                State = fencedState,
            }, CancellationToken.None);

            if (operationToCancel is not null)
                await DispatchOperationCancellationAsync(operationToCancel);

            if (decision.Admission is null)
            {
                if (terminalPrepared)
                    await DispatchPendingHistoryTerminalAsync();
                return;
            }

            var continuationState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
            continuationState.PendingHistoryTerminal = State.PendingHistoryTerminal?.Clone();
            if (pendingSteeringContinuation is not null)
            {
                continuationState.PendingSteeringContinuation =
                    pendingSteeringContinuation.Clone();
                continuationState.PendingSteeringContinuationId =
                    decision.Admission.ContinuationTurnId;
                continuationState.PendingSteeringContinuationExpiresAt =
                    pendingSteeringContinuationExpiresAt?.Clone();
            }

            await PersistDomainEventAsync(new NyxIdChatContinuationAdmissionCommittedEvent
            {
                Admission = decision.Admission.Clone(),
                State = continuationState,
            }, CancellationToken.None);
        }
        catch
        {
            if (pendingSteeringContinuation is not null &&
                !string.Equals(
                    State.PendingSteeringContinuation?.Ref,
                    pendingSteeringContinuation.Ref,
                    StringComparison.Ordinal))
            {
                await RevokePendingSteeringContinuationAsync(
                    pendingSteeringContinuation,
                    "nyxid chat steering admission did not commit");
            }

            throw;
        }

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();

        if (pendingSteeringContinuation is not null)
            await SchedulePendingSteeringContinuationExpiryAsync(CancellationToken.None);

        if (decision.StartContinuationNow)
            await DispatchSteeringContinuationAsync(command, decision.Admission);
    }

    [EventHandler]
    public async Task HandleRetryStepAsync(NyxIdChatRetryStepCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var decision = NyxIdChatControlCommands.Retry(
            State,
            command,
            CurrentCommittedVersion(),
            now);
        if (decision.ShouldCommit)
        {
            var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
            var terminalPrepared = PrepareHistoryTerminalOutbox(nextState);
            await PersistDomainEventAsync(new NyxIdChatStepControlCommittedEvent
            {
                Result = decision.Result.Clone(),
                State = nextState,
            }, CancellationToken.None);

            if (terminalPrepared)
                await DispatchPendingHistoryTerminalAsync();
        }

        if (!decision.ShouldDispatch || decision.NextCommand is null)
            return;

        await DispatchAuthorizedOperationAsync(
                decision.NextCommand,
                command.CorrelationId,
                now);
    }

    [EventHandler]
    public async Task HandleSkipStepAsync(NyxIdChatSkipStepCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var decision = NyxIdChatControlCommands.Skip(
            State,
            command,
            CurrentCommittedVersion(),
            Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        if (!decision.ShouldCommit)
            return;

        var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        var terminalPrepared = PrepareHistoryTerminalOutbox(nextState);

        await PersistDomainEventAsync(new NyxIdChatStepControlCommittedEvent
        {
            Result = decision.Result.Clone(),
            State = nextState,
        }, CancellationToken.None);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleInputRequestAsync(NyxIdChatInputRequestCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var decision = NyxIdChatNeedsYouDecisions.RequestInput(
            State,
            command,
            Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        if (!decision.ShouldCommit || decision.Resolution is null)
            return;

        await PersistDomainEventAsync(new NyxIdChatInputRequestedEvent
        {
            PendingInput = decision.Resolution.Clone(),
            State = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State),
        }, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleInputResolveAsync(NyxIdChatInputResolveCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var decision = NyxIdChatNeedsYouDecisions.ResolveInput(
            State,
            command,
            CurrentCommittedVersion(),
            Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        if (!decision.ShouldCommit || decision.Resolution is null)
            return;

        await PersistDomainEventAsync(new NyxIdChatInputResolutionCommittedEvent
        {
            Resolution = decision.Resolution.Clone(),
            State = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State),
        }, CancellationToken.None);

        if (decision.NextCommand is not null)
        {
            await DispatchAuthorizedOperationAsync(
                decision.NextCommand,
                command.CorrelationId,
                Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        }
    }

    [EventHandler]
    public async Task HandleApprovalResolveAsync(NyxIdChatApprovalResolveCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var decision = NyxIdChatNeedsYouDecisions.ResolveApproval(
            State,
            command,
            CurrentCommittedVersion(),
            Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        if (!decision.ShouldCommit || decision.Resolution is null)
            return;

        // A resolve at or after the deadline commits an expiry denial that
        // terminalizes the turn in the same decision, so the terminal history
        // outbox must be prepared here; live approvals keep the turn active
        // and this stays a no-op.
        var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        var terminalPrepared = PrepareHistoryTerminalOutbox(nextState);
        await PersistDomainEventAsync(new NyxIdChatApprovalResolutionCommittedEvent
        {
            Resolution = decision.Resolution.Clone(),
            State = nextState,
        }, CancellationToken.None);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();

        if (decision.NextCommand is not null)
        {
            await DispatchAuthorizedOperationAsync(
                decision.NextCommand,
                command.CorrelationId,
                Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleToolApprovalExpiredAsync(NyxIdChatToolApprovalExpiredSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var pending = State.PendingApproval;
        if (pending is null ||
            !string.Equals(
                pending.ApprovalRequestId,
                signal.ApprovalRequestId,
                StringComparison.Ordinal) ||
            pending.ExpiresAt is null ||
            signal.ExpectedExpiresAt is null ||
            !pending.ExpiresAt.Equals(signal.ExpectedExpiresAt))
        {
            return;
        }

        if (_timeProvider.GetUtcNow() < signal.ExpectedExpiresAt.ToDateTimeOffset())
        {
            await ScheduleToolApprovalExpiryAsync(CancellationToken.None);
            return;
        }

        var decision = NyxIdChatNeedsYouDecisions.ExpireApproval(
            State,
            signal,
            Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        if (!decision.ShouldCommit || decision.Resolution is null)
            return;

        var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        var terminalPrepared = PrepareHistoryTerminalOutbox(nextState);
        await PersistDomainEventAsync(new NyxIdChatApprovalResolutionCommittedEvent
        {
            Resolution = decision.Resolution.Clone(),
            State = nextState,
        }, CancellationToken.None);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();
    }

    private Task ScheduleToolApprovalExpiryAsync(CancellationToken ct)
    {
        var pending = State.PendingApproval;
        if (pending is null ||
            string.IsNullOrWhiteSpace(pending.ApprovalRequestId) ||
            pending.ExpiresAt is null)
        {
            return Task.CompletedTask;
        }

        var delay = pending.ExpiresAt.ToDateTimeOffset() - _timeProvider.GetUtcNow();
        if (delay < ActivationRecoveryDelay)
            delay = ActivationRecoveryDelay;
        return ScheduleSelfDurableTimeoutAsync(
            BuildStableIdentity("tool-approval-expiry", Id, pending.ApprovalRequestId),
            delay,
            new NyxIdChatToolApprovalExpiredSignal
            {
                ApprovalRequestId = pending.ApprovalRequestId,
                ExpectedExpiresAt = pending.ExpiresAt.Clone(),
            },
            ct: ct);
    }

    [EventHandler]
    public async Task HandleCanaryEffectFaultArmAsync(NyxIdChatCanaryEffectFaultArmCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!NyxIdChatCanaryEffectFaultDecisions.TryArm(
                State,
                command,
                CurrentCommittedVersion(),
                Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                out var next))
        {
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatCanaryEffectFaultArmedCommittedEvent
        {
            State = next,
        }, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleCanaryEffectFaultConsumedAsync(
        NyxIdChatCanaryEffectFaultConsumedSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!NyxIdChatCanaryEffectFaultDecisions.TryMarkConsumed(
                State,
                signal,
                Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                out var next))
        {
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatCanaryEffectFaultConsumedCommittedEvent
        {
            State = next,
        }, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleOperationDeliveryProbeDispatchRequestedAsync(
        NyxIdChatOperationDeliveryProbeDispatchRequested signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.ExpectedStateVersion != CurrentCommittedVersion() ||
            !KeysEqual(State.PendingOperationDeliveryProbe, signal.Key))
        {
            return;
        }

        await DispatchPendingOperationDeliveryProbeAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleOperationDeliveryStatusAsync(
        NyxIdChatTurnOperationDeliveryStatusSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!KeysEqual(State.PendingOperationDeliveryProbe, signal.Key) ||
            !TryResolveCurrentOperation(signal.Key, out var operation) ||
            !IsInFlight(operation.Phase))
        {
            return;
        }

        if (!signal.Admitted)
        {
            await PersistOperationDispatchFailureAsync(
                signal.Key,
                "NYXID_CHAT_OPERATION_DELIVERY_FENCED",
                "The chat operation was not admitted and was fenced against late delivery.");
            return;
        }

        if (signal.EffectDispatchWaterline is
            NyxIdChatEffectEvidence.Unspecified or
            NyxIdChatEffectEvidence.Confirmed)
        {
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatOperationDispatchedEvent
        {
            Key = signal.Key.Clone(),
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            EffectDispatchWaterline = signal.EffectDispatchWaterline,
        }, CancellationToken.None);
        await ScheduleOutstandingOperationStallCheckAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleActionContinueAsync(NyxIdChatActionContinueCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var decision = NyxIdChatBrowserActions.Continue(State, command, now);
        var terminalPrepared = false;
        if (!decision.ShouldCommit &&
            decision.Outcome == NyxIdChatTransitionOutcome.Rejected)
        {
            await PersistActionContinuationRejectionAsync(
                command,
                decision.ReasonCode,
                decision.SafeMessage);
            return;
        }
        if (decision.ShouldCommit)
        {
            var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
            if (decision.Outcome == NyxIdChatTransitionOutcome.Accepted &&
                decision.Admission.Status ==
                NyxIdChatContinuationAdmissionStatus.Accepted)
            {
                nextState.HistoryDeliveryReservation =
                    BuildActionContinuationHistoryReservation(command, decision.Admission);
                terminalPrepared = PrepareHistoryTerminalOutbox(nextState);
            }

            await PersistDomainEventAsync(new NyxIdChatContinuationAdmissionCommittedEvent
            {
                Admission = decision.Admission.Clone(),
                State = nextState,
            }, CancellationToken.None);
        }

        if (State.ContinuationAdmission is
            {
                Kind: NyxIdChatContinuationKind.Action,
                Status: NyxIdChatContinuationAdmissionStatus.Accepted,
            } admission &&
            string.Equals(
                State.ActiveTurn?.TurnId,
                admission.ContinuationTurnId,
                StringComparison.Ordinal) &&
            State.HistoryDeliveryReservation is
            { Dispatched: false } pendingReservation)
        {
            try
            {
                await ReserveHistoryDeliveryAsync(pendingReservation, CancellationToken.None);
                await PersistDomainEventAsync(
                    new NyxIdChatHistoryDeliveryReservationDispatchedEvent
                    {
                        DeliveryId = pendingReservation.DeliveryId,
                        SourceCommandId = pendingReservation.SourceCommandId,
                        DispatchedAt = Timestamp.FromDateTimeOffset(
                            _timeProvider.GetUtcNow()),
                    },
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Logger.LogWarning(
                    "NyxIdChat action continuation history reservation failed: actor={ActorId} delivery={DeliveryId} exceptionType={ExceptionType}",
                    Id,
                    pendingReservation.DeliveryId,
                    exception.GetType().Name);
                if (decision.NextCommand?.Key is not null)
                {
                    await PersistOperationDispatchFailureAsync(
                            decision.NextCommand.Key,
                            "NYXID_CHAT_HISTORY_RESERVATION_FAILED",
                            "The chat turn could not reserve its transcript delivery.",
                            exception);
                }
                return;
            }
        }

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();

        if (!decision.ShouldDispatch || decision.NextCommand is null)
            return;

        await DispatchFirstOperationAsync(
                decision.NextCommand,
                command.CorrelationId,
                now);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleRecoveryRequestedAsync(NyxIdChatRecoveryRequestedSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.ExpectedStateVersion != CurrentCommittedVersion() ||
            signal.Key is null ||
            !TryResolveCurrentOperation(signal.Key, out var operation) ||
            operation.Phase is not (NyxIdChatOperationPhase.Requested or
                NyxIdChatOperationPhase.Dispatched or
                NyxIdChatOperationPhase.Running))
        {
            return;
        }

        if (signal.Kind == NyxIdChatRecoveryKind.PostconditionRedispatch)
        {
            var command = NyxIdChatBrowserActions.TryBuildRecoveryDispatch(State, signal.Key);
            if (command is null)
                return;

            await DispatchFirstOperationAsync(
                    command,
                    ActiveInboundEnvelope?.Propagation?.CorrelationId ??
                    command.Key.OperationId,
                    Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
            return;
        }

        if (signal.Kind != NyxIdChatRecoveryKind.InterruptedOperationReconciliation)
            return;

        var recoveryResult = NyxIdChatTaskTransitionPolicy.BuildInterruptedRecoveryResult(
            State,
            signal.Key);
        if (recoveryResult is null)
            return;

        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(
            State,
            recoveryResult,
            now);
        if (decision.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return;

        var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        nextState.ProgressSequence = State.ProgressSequence + 1;
        nextState.UpdatedAt = now.Clone();
        var terminalPrepared = PrepareHistoryTerminalOutbox(nextState);
        await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
        {
            Result = recoveryResult,
            Task = nextState.ActiveTask.Clone(),
            Turn = nextState.ActiveTurn.Clone(),
            ProgressSequence = nextState.ProgressSequence,
            State = nextState,
        }, CancellationToken.None);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();

        if (decision.NextCommand is null)
            return;

        await DispatchAuthorizedOperationAsync(
            decision.NextCommand,
            ActiveInboundEnvelope?.Propagation?.CorrelationId ??
            decision.NextCommand.Key.OperationId,
            now);
    }

    [EventHandler]
    public async Task HandleOperationProgressAsync(NyxIdChatOperationProgressSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!IsValidOperationProgress(signal) ||
            !TryResolveCurrentOperation(signal.Key, out var operation) ||
            !IsInFlight(operation.Phase) ||
            State.ControlFence is not null ||
            State.ActiveTurn is null ||
            State.ActiveTurn.Status is NyxIdChatTurnStatus.Succeeded or
                NyxIdChatTurnStatus.Failed or
                NyxIdChatTurnStatus.Stopped or
                NyxIdChatTurnStatus.Blocked ||
            signal.Sequence <= operation.LatestProgressSequence ||
            !IsValidPhaseTransition(State, signal))
        {
            return;
        }

        var wasStalled = operation.StalledAt is not null;
        var hadPendingStepChanged = operation.PendingStepChangedProgressSequence > 0;
        var committedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var progressed = new NyxIdChatOperationProgressedEvent
        {
            Progress = signal.Clone(),
            ProgressSequence = State.ProgressSequence + 1,
            CommittedAt = committedAt,
            StepChangeKind = ResolveProgressStepChangeKind(operation, signal, committedAt),
        };
        progressed.State = ApplyOperationProgressed(State, progressed);
        await PersistDomainEventAsync(progressed, CancellationToken.None);

        if (!hadPendingStepChanged)
            await ScheduleOutstandingOperationStepChangedAsync(CancellationToken.None);
        if (wasStalled)
            await ScheduleOutstandingOperationStallCheckAsync(CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleOperationStepChangedDueAsync(
        NyxIdChatOperationStepChangedDueSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!TryResolveCurrentOperation(signal.Key, out var operation) ||
            !IsInFlight(operation.Phase) ||
            operation.PendingStepChangedProgressSequence <= 0 ||
            operation.StepChangedDueAt is null ||
            !TimestampsEqual(signal.ExpectedDueAt, operation.StepChangedDueAt))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (now < operation.StepChangedDueAt.ToDateTimeOffset())
        {
            await ScheduleOperationStepChangedAsync(operation, CancellationToken.None);
            return;
        }

        var committed = new NyxIdChatOperationStepChangedCommittedEvent
        {
            Key = signal.Key.Clone(),
            GenuineProgressSequence = operation.PendingStepChangedProgressSequence,
            CommittedAt = Timestamp.FromDateTimeOffset(now),
            ProgressSequence = State.ProgressSequence + 1,
        };
        committed.State = ApplyOperationStepChangedCommitted(State, committed);
        await PersistDomainEventAsync(committed, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleOperationStallCheckAsync(NyxIdChatOperationStallCheckSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!TryResolveCurrentOperation(signal.Key, out var operation) ||
            !IsInFlight(operation.Phase) ||
            operation.LastProgressAt is null ||
            operation.StalledAt is not null)
        {
            return;
        }

        if (signal.ExpectedProgressSequence != operation.LatestProgressSequence ||
            !TimestampsEqual(signal.ExpectedLastProgressAt, operation.LastProgressAt))
        {
            await ScheduleOperationStallCheckAsync(operation, CancellationToken.None);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var stallAt = operation.LastProgressAt.ToDateTimeOffset() + OperationStallThreshold;
        if (now < stallAt)
        {
            await ScheduleOperationStallCheckAsync(operation, CancellationToken.None);
            return;
        }

        var next = State.Clone();
        var task = next.ActiveTask;
        if (task is null)
            return;

        var step = task.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, signal.Key));
        if (step?.Operation is null)
            return;

        step.Operation.StalledAt = Timestamp.FromDateTimeOffset(stallAt);
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);
        step.UpdatedAt = Timestamp.FromDateTimeOffset(now);
        task.UpdatedAt = step.UpdatedAt.Clone();
        next.ProgressSequence = checked(next.ProgressSequence + 1);
        next.UpdatedAt = step.UpdatedAt.Clone();
        next = NyxIdChatNeedsYouDecisions.RefreshAttention(next);
        await PersistDomainEventAsync(new NyxIdChatOperationStalledEvent
        {
            Key = signal.Key.Clone(),
            ExpectedProgressSequence = signal.ExpectedProgressSequence,
            StalledAt = step.Operation.StalledAt.Clone(),
            ProgressSequence = next.ProgressSequence,
            State = next,
        }, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleOperationResultAsync(NyxIdChatOperationResultSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (HasResultAcknowledgementFence(State, signal))
        {
            await DispatchOperationResultAcknowledgementAsync(signal, CancellationToken.None);
            return;
        }

        if (!TryResolveCurrentOperation(signal.Key, out var currentOperation))
            return;

        var acknowledgementRequired = RequiresResultAcknowledgement(State, signal);
        var committedSignal = signal;
        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var lateEvidence = NyxIdChatControlCommands.ReconcileLateOperationEvidence(
            State,
            signal,
            now);
        var fencedVerification = lateEvidence.IsFencedOperation &&
                                 signal.ResultCase ==
                                 NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification;
        if (lateEvidence.IsFencedOperation && !fencedVerification)
        {
            if (!lateEvidence.ShouldCommit)
            {
                if (acknowledgementRequired)
                {
                    await CommitFencedPostconditionResultConsumptionAsync(
                        signal,
                        currentOperation,
                        now);
                }
                return;
            }

            var lateState = lateEvidence.State;
            NyxIdChatOperationDispatchCommand? verification = null;
            if (lateEvidence.OperationPhase == NyxIdChatOperationPhase.Uncertain &&
                lateEvidence.ExternalEffect == NyxIdChatEffectEvidence.MayHaveChanged)
            {
                verification = NyxIdChatTaskLifecycle.PlanFencedEffectVerification(
                    lateState,
                    signal.Key,
                    now);
            }
            lateState = NyxIdChatNeedsYouDecisions.RefreshAttention(lateState);
            if (acknowledgementRequired)
                RememberResultAcknowledgementFence(lateState, signal);
            await PersistDomainEventAsync(new NyxIdChatLateOperationEvidenceCommittedEvent
            {
                Key = signal.Key.Clone(),
                OperationPhase = lateEvidence.OperationPhase,
                ExternalEffect = lateEvidence.ExternalEffect,
                ToolReceipt = BuildDurableReceiptEvidence(signal.Tool?.Receipt),
                TerminalCode = lateEvidence.TerminalCode,
                SafeMessage = lateEvidence.SafeMessage,
                ProgressSequence = lateState.ProgressSequence,
                CommittedAt = now.Clone(),
                State = lateState,
            }, CancellationToken.None);
            if (acknowledgementRequired)
                await DispatchOperationResultAcknowledgementAsync(signal, CancellationToken.None);
            if (CanDispatchPendingSteeringContinuation(State))
                await DispatchPendingSteeringContinuationAsync(CancellationToken.None);
            if (verification is not null)
            {
                await DispatchAuthorizedOperationAsync(
                    verification,
                    ActiveInboundEnvelope?.Propagation?.CorrelationId ??
                    signal.Key.OperationId,
                    now);
            }
            return;
        }

        if (signal.ResultCase ==
                NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition &&
            State.PendingActions.Any(action => string.Equals(
                action.ActionRequestId,
                signal.ActionPostcondition.ActionRequestId,
                StringComparison.Ordinal)))
        {
            var actionDecision = NyxIdChatBrowserActions.ReconcilePostcondition(
                State,
                signal,
                now);
            if (actionDecision.ShouldCommit)
            {
                var actionState = NyxIdChatNeedsYouDecisions.RefreshAttention(actionDecision.State);
                var actionTerminalPrepared = PrepareHistoryTerminalOutbox(actionState);
                if (acknowledgementRequired)
                    RememberResultAcknowledgementFence(actionState, signal);

                await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
                {
                    Result = BuildDurableResultEvidence(signal),
                    Task = actionState.ActiveTask.Clone(),
                    Turn = actionState.ActiveTurn.Clone(),
                    ProgressSequence = actionState.ProgressSequence,
                    State = actionState,
                }, CancellationToken.None);
                if (acknowledgementRequired)
                    await DispatchOperationResultAcknowledgementAsync(signal, CancellationToken.None);

                if (actionTerminalPrepared)
                    await DispatchPendingHistoryTerminalAsync();

                if (!actionDecision.ShouldDispatch || actionDecision.NextCommand is null)
                    return;

                await DispatchAuthorizedOperationAsync(
                        actionDecision.NextCommand,
                        ActiveInboundEnvelope?.Propagation?.CorrelationId ??
                        signal.Key.OperationId,
                        now);
                return;
            }

            committedSignal = BuildRejectedPostconditionResult(
                signal,
                actionDecision.ReasonCode,
                actionDecision.SafeMessage);
        }

        if (signal.Tool?.Receipt is
            {
                Status: AgentToolReceiptStatus.AuthorizationRequired,
                AuthorizationRequired: not null,
            })
        {
            if (NyxIdChatActionContinuationCorrelation.TryMatch(
                    State,
                    State.ActiveTask,
                    State.ActiveTurn,
                    signal.Key,
                    out _))
            {
                committedSignal = new NyxIdChatOperationResultSignal
                {
                    Key = signal.Key.Clone(),
                    Failure = new NyxIdChatOperationFailure
                    {
                        FailureCode = NyxIdChatTurnOperationExecutor
                            .AuthorizationContinuationCapabilityUnavailableCode,
                        SafeMessage = NyxIdChatTurnOperationExecutor
                            .AuthorizationContinuationCapabilityUnavailableMessage,
                        ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                    },
                };
            }
            else
            {
                try
                {
                    var actionDecision = NyxIdChatBrowserActions.RequestAuthorization(
                        State,
                        signal,
                        Services.GetRequiredService<NyxIdAssistantActionRegistry>(),
                        now);
                    if (!actionDecision.ShouldCommit)
                        return;

                    var actionState = NyxIdChatNeedsYouDecisions.RefreshAttention(actionDecision.State);
                    var authorizationTerminalPrepared = PrepareHistoryTerminalOutbox(actionState);
                    RememberResultAcknowledgementFence(actionState, signal);

                    await PersistDomainEventAsync(new NyxIdChatActionRequestedEvent
                    {
                        Request = actionDecision.Request.Clone(),
                        Task = actionState.ActiveTask.Clone(),
                        OriginTurn = actionState.ActiveTurn.Clone(),
                        State = actionState,
                    }, CancellationToken.None);
                    await DispatchOperationResultAcknowledgementAsync(signal, CancellationToken.None);

                    if (authorizationTerminalPrepared)
                        await DispatchPendingHistoryTerminalAsync();
                    return;
                }
                catch (NyxIdAssistantActionRegistryException exception)
                {
                    signal = new NyxIdChatOperationResultSignal
                    {
                        Key = signal.Key.Clone(),
                        Failure = new NyxIdChatOperationFailure
                        {
                            FailureCode = exception.Code,
                            SafeMessage = "The requested NyxID action is unavailable.",
                            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                        },
                    };
                    committedSignal = signal;
                }
            }
        }

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(
            State,
            committedSignal,
            now);
        if (decision.Outcome != NyxIdChatTransitionOutcome.Accepted &&
            acknowledgementRequired &&
            committedSignal.ResultCase !=
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure)
        {
            committedSignal = BuildRejectedPostconditionResult(
                signal,
                decision.ReasonCode,
                decision.SafeMessage);
            decision = NyxIdChatTaskLifecycle.ApplyOperationResult(
                State,
                committedSignal,
                now);
        }
        if (decision.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return;

        if (signal.Key is not null)
        {
            NyxIdChatCanaryEffectFaultDecisions.TryAttachToDirectToolDispatch(
                decision.State,
                signal.Key,
                decision.NextCommand,
                now);
        }
        var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        if (fencedVerification)
        {
            nextState.ActiveTask.Status = NyxIdChatTaskStatus.Stopped;
            nextState.ActiveTask.ActiveStepId = string.Empty;
            nextState.ActiveTask.ActiveOperationId = string.Empty;
            nextState.ActiveTurn.Status = NyxIdChatTurnStatus.Stopped;
            if (signal.ToolVerification.Disposition !=
                    NyxIdChatToolVerificationDisposition.Unavailable &&
                nextState.ContinuationAdmission?.Status ==
                    NyxIdChatContinuationAdmissionStatus.AcceptedForLater)
            {
                nextState.ContinuationAdmission.Status =
                    NyxIdChatContinuationAdmissionStatus.Accepted;
                nextState.ContinuationAdmission.ReasonCode =
                    NyxIdChatControlCommands.SteeringAccepted;
                nextState.ContinuationAdmission.SafeMessage =
                    "Steering can continue after exact effect verification.";
            }
        }
        nextState.ProgressSequence = State.ProgressSequence + 1;
        nextState.UpdatedAt = now.Clone();
        var currentStep = State.ActiveTask?.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, committedSignal.Key));
        if (currentStep is null ||
            !OperationTurnMatchesReconciledState(
                State,
                nextState,
                currentStep,
                committedSignal.Key))
        {
            return;
        }
        var terminalText = committedSignal.ResultCase ==
                           NyxIdChatOperationResultSignal.ResultOneofCase.Llm
            ? committedSignal.Llm.Content
            : null;
        var terminalPrepared = !fencedVerification &&
                               PrepareHistoryTerminalOutbox(nextState, terminalText);
        if (acknowledgementRequired)
            RememberResultAcknowledgementFence(nextState, signal);

        await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
        {
            Result = BuildDurableResultEvidence(committedSignal),
            Task = nextState.ActiveTask.Clone(),
            Turn = nextState.ActiveTurn.Clone(),
            ProgressSequence = nextState.ProgressSequence,
            State = nextState,
            RefinesExistingTerminal = fencedVerification,
        }, CancellationToken.None);
        if (acknowledgementRequired)
            await DispatchOperationResultAcknowledgementAsync(signal, CancellationToken.None);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();

        if (State.PendingApproval is not null)
            await ScheduleToolApprovalExpiryAsync(CancellationToken.None);

        if (fencedVerification && CanDispatchPendingSteeringContinuation(State))
            await DispatchPendingSteeringContinuationAsync(CancellationToken.None);

        if (decision.InputRequest is not null)
        {
            await DispatchInputRequestContinuationAsync(
                decision.InputRequest,
                CancellationToken.None);
        }

        if (decision.NextCommand is null)
            return;

        await DispatchAuthorizedOperationAsync(
            decision.NextCommand,
            ActiveInboundEnvelope?.Propagation?.CorrelationId ??
            currentOperation.Key?.OperationId ??
            decision.NextCommand.Key?.OperationId ??
            string.Empty,
            now);
    }

    private static NyxIdChatOperationResultSignal BuildDurableResultEvidence(
        NyxIdChatOperationResultSignal signal)
    {
        var durable = new NyxIdChatOperationResultSignal { Key = signal.Key?.Clone() };
        switch (signal.ResultCase)
        {
            case NyxIdChatOperationResultSignal.ResultOneofCase.Llm:
                durable.Llm = new NyxIdChatLLMOperationResult
                {
                    FinishReason = signal.Llm.FinishReason,
                    Usage = signal.Llm.Usage?.Clone(),
                    ToolCatalogCaptured = signal.Llm.ToolCatalogCaptured,
                };
                durable.Llm.AvailableToolNames.AddRange(signal.Llm.AvailableToolNames);
                durable.Llm.ToolCalls.AddRange(signal.Llm.ToolCalls.Select(static call =>
                    new NyxIdChatToolCall
                    {
                        CallId = call.CallId,
                        ToolName = call.ToolName,
                        Safety = call.Safety?.Clone(),
                        NyxIdProvenance = call.NyxIdProvenance?.Clone(),
                        Presentation = NyxIdChatDurableToolPresentation.Snapshot(
                            call.Presentation,
                            call.ToolName),
                    }));
                break;
            case NyxIdChatOperationResultSignal.ResultOneofCase.Tool:
                durable.Tool = new NyxIdChatToolOperationResult
                {
                    ExternalEffect = signal.Tool.ExternalEffect,
                };
                if (BuildDurableReceiptEvidence(signal.Tool.Receipt) is { } receipt)
                    durable.Tool.Receipt = receipt;
                break;
            case NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition:
                durable.ActionPostcondition = signal.ActionPostcondition.Clone();
                break;
            case NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification:
                durable.ToolVerification = signal.ToolVerification.Clone();
                break;
            case NyxIdChatOperationResultSignal.ResultOneofCase.Failure:
                durable.Failure = signal.Failure.Clone();
                break;
        }

        return durable;
    }

    internal static AgentToolReceipt? BuildDurableReceiptEvidence(AgentToolReceipt? receipt)
    {
        if (receipt is null)
            return null;

        var durable = new AgentToolReceipt
        {
            CallId = receipt.CallId,
            ToolName = receipt.ToolName,
            Status = receipt.Status,
            ApprovalMode = receipt.ApprovalMode,
            NyxIdApprovalDecisionMode = receipt.NyxIdApprovalDecisionMode,
            IsDestructive = receipt.IsDestructive,
            SideEffectKind = receipt.SideEffectKind,
            Effect = receipt.Effect,
            SubjectKind = receipt.SubjectKind,
            SubjectId = receipt.SubjectId,
            SubjectVersion = receipt.SubjectVersion,
            SubjectHash = receipt.SubjectHash,
            ApprovalRequestId = receipt.ApprovalRequestId,
            ErrorCode = NyxIdChatPublicToolReceiptResult.NormalizeErrorCode(receipt.ErrorCode),
            ErrorMessage = string.Empty,
            ProviderResourceId = receipt.ProviderResourceId,
            MutationStage = receipt.MutationStage,
            NyxIdApprovalTerminalOutcome = receipt.NyxIdApprovalTerminalOutcome,
            ResultJson = NyxIdChatPublicToolReceiptResult.Project(receipt),
        };
        if (receipt.ManagedWorkflowHandoff is not null)
            durable.ManagedWorkflowHandoff = receipt.ManagedWorkflowHandoff.Clone();
        if (receipt.WorkflowRunDelivery is not null)
            durable.WorkflowRunDelivery = receipt.WorkflowRunDelivery.Clone();
        if (receipt.AuthorizationRequired is not null)
            durable.AuthorizationRequired = receipt.AuthorizationRequired.Clone();
        if (receipt.ExactServiceApproval is not null)
            durable.ExactServiceApproval = receipt.ExactServiceApproval.Clone();
        return durable;
    }

    private NyxIdChatConversationGAgentState BuildStartedState(
        NyxIdChatStartTurnCommand command,
        NyxIdChatOperationKey operationKey,
        AgentProfileTurnAuthorityState? turnAuthority,
        NyxIdChatTurnIntent intent,
        Timestamp now)
    {
        var previousTask = command.AddedBy != NyxIdChatStepAddedBy.Initial &&
                           State.ActiveTask is not null &&
                           string.Equals(
                               State.ActiveTask.TaskId,
                               command.TaskId.Trim(),
                               StringComparison.Ordinal)
            ? State.ActiveTask
            : null;
        var turn = new NyxIdChatTurnState
        {
            TurnId = command.TurnId.Trim(),
            TaskId = command.TaskId.Trim(),
            ClientRequestId = command.ClientRequestId.Trim(),
            CommandId = command.CommandId.Trim(),
            Status = NyxIdChatTurnStatus.Active,
            Prompt = command.Prompt,
            CreatedAt = now.Clone(),
            AgentProfileTurnAuthority = turnAuthority?.Clone(),
            Intent = intent,
        };
        turn.InputParts.AddRange(command.InputParts.Select(SanitizeInputPart));

        var step = new NyxIdChatTaskStepState
        {
            StepId = operationKey.StepId,
            Order = previousTask?.Steps.Count > 0
                ? previousTask.Steps.Max(static item => item.Order) + 1
                : 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = "Generate the next assistant response.",
            Source = new NyxIdChatStepSource
            {
                Llm = new NyxIdChatLLMStepSource
                {
                    Model = command.LlmControl?.ModelOverride ?? string.Empty,
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            RetryInputRebuildable = true,
            AddedBy = command.AddedBy == NyxIdChatStepAddedBy.Unspecified
                ? NyxIdChatStepAddedBy.Initial
                : command.AddedBy,
            Operation = new NyxIdChatOperationState
            {
                Key = operationKey.Clone(),
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = now.Clone(),
            },
            UpdatedAt = now.Clone(),
        };
        step.AvailableActions = NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(step);

        var task = new NyxIdChatTaskState
        {
            TaskId = command.TaskId.Trim(),
            TurnId = command.TurnId.Trim(),
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = step.StepId,
            ActiveOperationId = operationKey.OperationId,
            CreatedAt = previousTask?.CreatedAt?.Clone() ?? now.Clone(),
            UpdatedAt = now.Clone(),
            SchemaVersion = 5,
            ActorId = Id,
            PlanId = string.IsNullOrWhiteSpace(command.PlanId)
                ? previousTask?.PlanId ?? command.TaskId.Trim()
                : command.PlanId.Trim(),
            PlanRevision = previousTask?.PlanRevision ?? Math.Max(1, command.PlanRevision),
            PlanRevisionHistoryStart = previousTask?.PlanRevisionHistoryStart ?? 0,
            Title = string.IsNullOrWhiteSpace(previousTask?.Title)
                ? "Complete the requested assistant task"
                : previousTask.Title,
        };
        if (previousTask is not null)
        {
            task.Steps.AddRange(previousTask.Steps.Select(static item => item.Clone()));
            task.PlanRevisions.AddRange(
                previousTask.PlanRevisions.Select(static revision => revision.Clone()));
        }
        task.Steps.Add(step);
        if (previousTask is null)
        {
            NyxIdChatPlanRevisions.CommitInitial(task, now, step);
        }
        else if (step.AddedBy != NyxIdChatStepAddedBy.Steering)
        {
            NyxIdChatPlanRevisions.CommitChange(
                task,
                NyxIdChatPlanRevisionCause.ScopeResolution,
                now,
                [step]);
        }
        else
        {
            var cancelledSteps = ResolveSteeringCancelledSteps(task, now);
            NyxIdChatPlanRevisions.CommitChange(
                task,
                NyxIdChatPlanRevisionCause.Steering,
                now,
                [step],
                cancelledSteps);
        }

        var next = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = Id,
            ScopeId = command.ScopeId.Trim(),
            OwnerSubject = State.OwnerSubject,
            RoleConfiguration = State.RoleConfiguration?.Clone(),
            AgentProfile = State.AgentProfile?.Clone(),
            ContextAttachments = State.ContextAttachments?.Clone(),
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            ProgressSequence = State.ProgressSequence + 1,
            UpdatedAt = now.Clone(),
        };
        next.PendingHistoryInitialization = State.PendingHistoryInitialization?.Clone();
        next.HistoryInitializationOperationId = State.HistoryInitializationOperationId;
        next.PendingHistoryTerminal = State.PendingHistoryTerminal?.Clone();
        next.PendingSteeringContinuation = State.PendingSteeringContinuation?.Clone();
        next.PendingSteeringContinuationId = State.PendingSteeringContinuationId;
        next.PendingSteeringContinuationExpiresAt =
            State.PendingSteeringContinuationExpiresAt?.Clone();
        next.RecentTerminalTurns.AddRange(
            State.RecentTerminalTurns.Select(static summary => summary.Clone()));
        next.RecentStepControlResults.AddRange(
            State.RecentStepControlResults.Select(static result => result.Clone()));
        next.LatestStepControlResult = State.LatestStepControlResult?.Clone();
        next.RecentInputResolutions.AddRange(
            State.RecentInputResolutions.Select(static result => result.Clone()));
        next.LatestInputResolution = State.LatestInputResolution?.Clone();
        next.RecentApprovalResolutions.AddRange(
            State.RecentApprovalResolutions.Select(static result => result.Clone()));
        next.LatestApprovalResolution = State.LatestApprovalResolution?.Clone();
        next.PendingActions.AddRange(
            State.PendingActions.Select(static action => action.Clone()));
        next.RecentActions.AddRange(
            State.RecentActions.Select(static action => action.Clone()));
        next.ResultAcknowledgementFences.AddRange(
            State.ResultAcknowledgementFences.Select(static fence => fence.Clone()));
        if (State.ContinuationAdmission is not null &&
            string.Equals(
                State.ContinuationAdmission.ContinuationTurnId,
                command.TurnId.Trim(),
                StringComparison.Ordinal))
        {
            next.ContinuationAdmission = State.ContinuationAdmission.Clone();
            next.ContinuationAdmission.Status = NyxIdChatContinuationAdmissionStatus.Started;
        }
        return next;
    }

    private IReadOnlyCollection<NyxIdChatTaskStepState> ResolveSteeringCancelledSteps(
        NyxIdChatTaskState task,
        Timestamp now)
    {
        var fence = State.ControlFence;
        if (fence is null ||
            fence.Kind != NyxIdChatControlKind.Steering ||
            string.IsNullOrWhiteSpace(fence.StepId) ||
            !string.Equals(fence.TaskId, task.TaskId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A steering revision requires the committed fence step identity.");
        }

        var fencedStep = task.Steps.SingleOrDefault(candidate =>
            string.Equals(candidate.StepId, fence.StepId, StringComparison.Ordinal));
        if (fencedStep is null)
        {
            throw new InvalidOperationException(
                "The committed steering fence step does not belong to the active task.");
        }

        if (fencedStep.Status != NyxIdChatStepStatus.Cancelled)
            return [];

        var cancelledSteps = new List<NyxIdChatTaskStepState> { fencedStep };
        var cancelledStepIds = new HashSet<string>(StringComparer.Ordinal)
        {
            fencedStep.StepId,
        };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var dependent in task.Steps.Where(candidate =>
                         candidate.Status is
                             NyxIdChatStepStatus.Planned or
                             NyxIdChatStepStatus.Waiting &&
                         candidate.DependsOn.Any(cancelledStepIds.Contains)))
            {
                dependent.Status = NyxIdChatStepStatus.Cancelled;
                dependent.ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
                dependent.FailureCode = fence.ReasonCode;
                dependent.SafeMessage = fence.SafeMessage;
                dependent.AvailableActions = new NyxIdChatAvailableActions();
                dependent.UpdatedAt = now.Clone();
                if (dependent.Operation is not null)
                {
                    dependent.Operation.Phase = NyxIdChatOperationPhase.Cancelled;
                    dependent.Operation.TerminalCode = fence.ReasonCode;
                    dependent.Operation.SafeMessage = fence.SafeMessage;
                    dependent.Operation.CompletedAt = now.Clone();
                }

                if (cancelledStepIds.Add(dependent.StepId))
                {
                    cancelledSteps.Add(dependent);
                    changed = true;
                }
            }
        }

        return cancelledSteps;
    }

    private static Aevatar.AI.Abstractions.ChatContentPart SanitizeInputPart(
        Aevatar.AI.Abstractions.ChatContentPart source)
    {
        var safe = source.Clone();
        safe.DataBase64 = string.Empty;
        return safe;
    }

    private Aevatar.AI.Abstractions.ChatRequestEvent BuildTransientChatRequest(
        NyxIdChatStartTurnCommand command)
    {
        var request = new Aevatar.AI.Abstractions.ChatRequestEvent
        {
            Prompt = BuildExecutionPrompt(command),
            SessionId = command.TurnId.Trim(),
            ScopeId = command.ScopeId.Trim(),
            CommandAttemptId = command.CommandId.Trim(),
            ToolContext = BuildActorOwnedToolContext(command.ToolContext).ToPayload(),
            LlmControl = command.LlmControl?.Clone(),
            ContextAttachments = State.ContextAttachments?.Clone(),
        };
        request.InputParts.AddRange(command.InputParts.Select(static part => part.Clone()));
        return request;
    }

    private async Task<AgentProfileTurnAuthorityState?> PrepareAgentProfileTurnAuthorityAsync(
        NyxIdChatStartTurnCommand command)
    {
        var profile = State.AgentProfile;
        if (profile is null)
            return null;

        if (_turnCatalogMaterializer is null)
        {
            return profile.ActivationMode == AgentProfileActivationMode.Shadow
                ? null
                : RestrictedEmptyAuthority(
                    command.TurnId,
                    AgentProfileTurnDegradationReason.MaterializerUnavailable);
        }

        try
        {
            var toolContext = LLMControlContextMapper.FromPayload(command.LlmControl)
                .ToToolContext(BuildActorOwnedToolContext(command.ToolContext));
            var llmControl = LLMControlContextMapper.FromPayload(command.LlmControl);
            var preparation = await _turnCatalogMaterializer.PrepareNyxIdChatAsync(
                    profile,
                    command.TurnId.Trim(),
                    BuildExecutionPrompt(command),
                    registeredTools: [],
                    toolContext,
                    llmControl,
                    CancellationToken.None);
            Logger.LogInformation(
                "Agent profile turn authority prepared. turn={TurnId} activation={ActivationMode} kind={AuthorityKind} ceilingCount={CeilingCount} diagnostics={Diagnostics}",
                command.TurnId,
                profile.ActivationMode,
                preparation.Authority.AuthorityKind,
                preparation.Authority.AuthorityCeilingToolNames.Count,
                string.Join(
                    ",",
                    preparation.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Code}:{diagnostic.Detail}")));
            return profile.ActivationMode == AgentProfileActivationMode.Shadow
                ? null
                : preparation.Authority;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(
                exception,
                "Agent profile turn authority preparation failed closed. turn={TurnId}",
                command.TurnId);
            return profile.ActivationMode == AgentProfileActivationMode.Shadow
                ? null
                : RestrictedEmptyAuthority(
                    command.TurnId,
                    AgentProfileTurnDegradationReason.MaterializationFailed);
        }
    }

    private async Task<NyxIdChatTurnIntent> ClassifyTurnIntentAsync(
        NyxIdChatStartTurnCommand command,
        AgentProfileTurnAuthorityState? turnAuthority)
    {
        if (turnAuthority is not null)
        {
            return turnAuthority.CandidateRoute?.IntentId switch
            {
                NyxIdChatTurnIntentClassifier.ServiceConnectIntentId =>
                    NyxIdChatTurnIntent.ServiceConnect,
                NyxIdChatTurnIntentClassifier.KeyCreateIntentId =>
                    NyxIdChatTurnIntent.KeyCreate,
                NyxIdChatTurnIntentClassifier.KeyRotateIntentId =>
                    NyxIdChatTurnIntent.KeyRotate,
                NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId =>
                    NyxIdChatTurnIntent.WorkflowAuthoring,
                _ => NyxIdChatTurnIntent.Unspecified,
            };
        }

        if (_turnIntentClassifier is null)
            return NyxIdChatTurnIntent.Unspecified;

        try
        {
            return await _turnIntentClassifier.ClassifyAsync(
                command.TurnId.Trim(),
                BuildExecutionPrompt(command),
                LLMControlContextMapper.FromPayload(command.LlmControl),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(
                exception,
                "NyxID chat turn intent classification failed. turn={TurnId}",
                command.TurnId);
            return NyxIdChatTurnIntent.Unspecified;
        }
    }

    private AgentToolExecutionContext BuildActorOwnedToolContext(
        AgentToolExecutionContextPayload? payload) =>
        AgentToolExecutionContextMapper.FromPayload(payload) with
        {
            ExecutionOwner = AgentToolExecutionOwners.Actor(Id),
        };

    private static AgentProfileTurnAuthorityState RestrictedEmptyAuthority(
        string sessionId,
        AgentProfileTurnDegradationReason reason) =>
        new()
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = sessionId.Trim(),
                Attempt = 1,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty,
            DegradationReasons = { reason },
        };

    private static NyxIdChatConversationGAgentState ApplyTurnStarted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatTurnStartedEvent evt) =>
        evt.State?.Clone() ?? current;

    private static NyxIdChatConversationGAgentState ApplyAgentProfileBound(
        NyxIdChatConversationGAgentState current,
        AgentProfileBoundEvent evt)
    {
        if (evt.Profile is null)
            throw new InvalidOperationException("Agent profile binding events require a complete snapshot.");
        if (!AgentProfileSnapshotCodec.Verify(evt.Profile))
            throw new InvalidOperationException("Agent profile binding events require a valid digest.");
        if (current.AgentProfile is not null)
        {
            if (!AgentProfileSnapshotCodec.ByteEquivalent(current.AgentProfile, evt.Profile))
                throw new InvalidOperationException("A bound agent profile cannot be replaced.");
            return current;
        }

        var next = current.Clone();
        next.AgentProfile = evt.Profile.Clone();
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyContextAttachmentsBound(
        NyxIdChatConversationGAgentState current,
        ConversationContextAttachmentsBoundEvent evt)
    {
        var incoming = evt.Attachments ?? new ConversationContextAttachmentSet();
        if (!ConversationContextAttachmentAdmission.TryNormalize(incoming, out var normalized))
            throw new InvalidOperationException("Conversation context attachment binding is invalid.");
        if (!ConversationContextAttachmentAdmission.HasAttachments(normalized))
            return current;
        if (current.ContextAttachments is not null)
        {
            if (!ConversationContextAttachmentAdmission.ByteEquivalent(current.ContextAttachments, normalized))
                throw new InvalidOperationException("A conversation cannot replace its context attachments.");
            return current;
        }

        var next = current.Clone();
        next.ContextAttachments = normalized;
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyConversationCreationStarted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatConversationCreationStartedEvent evt)
    {
        var next = current.Clone();
        var currentOwner = NormalizeOptional(current.OwnerSubject);
        var eventOwner = NormalizeOptional(evt.OwnerSubject);
        if (!string.IsNullOrWhiteSpace(current.ConversationActorId) &&
            !string.Equals(currentOwner, eventOwner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A NyxIdChat conversation owner cannot be replaced or claimed after creation.");
        }
        next.ConversationActorId = evt.ActorId;
        next.ScopeId = evt.ScopeId;
        next.OwnerSubject = eventOwner ?? string.Empty;
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyConversationRegistrationAccepted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatConversationRegistrationAcceptedEvent evt)
    {
        if (evt.State is null ||
            !string.Equals(evt.State.ConversationActorId, evt.ActorId, StringComparison.Ordinal) ||
            !string.Equals(evt.State.ScopeId, evt.ScopeId, StringComparison.Ordinal))
        {
            return current;
        }

        return evt.State.Clone();
    }

    private static NyxIdChatConversationGAgentState ApplyPendingCreationFirstTurnFinalized(
        NyxIdChatConversationGAgentState current,
        NyxIdChatPendingCreationFirstTurnFinalizedEvent evt)
    {
        if (current.PendingCreationFirstTurn is null ||
            !string.Equals(current.ConversationActorId, evt.ConversationActorId, StringComparison.Ordinal) ||
            !string.Equals(current.PendingCreationFirstTurnId, evt.TurnId, StringComparison.Ordinal) ||
            !string.Equals(current.PendingCreationFirstTurn.Ref, evt.CredentialRef, StringComparison.Ordinal))
        {
            return current;
        }

        var next = current.Clone();
        next.PendingCreationFirstTurn = null;
        next.PendingCreationFirstTurnId = string.Empty;
        next.UpdatedAt = evt.CommittedAt?.Clone();
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyPendingSteeringContinuationFinalized(
        NyxIdChatConversationGAgentState current,
        NyxIdChatPendingSteeringContinuationFinalizedEvent evt)
    {
        if (current.PendingSteeringContinuation is null ||
            evt.State is null ||
            !string.Equals(current.ConversationActorId, evt.ConversationActorId,
                StringComparison.Ordinal) ||
            !string.Equals(current.PendingSteeringContinuationId, evt.ContinuationTurnId,
                StringComparison.Ordinal) ||
            !string.Equals(current.PendingSteeringContinuation.Ref, evt.CredentialRef,
                StringComparison.Ordinal) ||
            evt.State.PendingSteeringContinuation is not null ||
            !string.IsNullOrWhiteSpace(evt.State.PendingSteeringContinuationId) ||
            evt.State.PendingSteeringContinuationExpiresAt is not null)
        {
            return current;
        }

        return evt.State.Clone();
    }

    private static NyxIdChatConversationGAgentState ApplyHistoryInitializationDispatched(
        NyxIdChatConversationGAgentState current,
        NyxIdChatHistoryInitializationDispatchedEvent evt)
    {
        var pending = current.PendingHistoryInitialization;
        if (pending is null ||
            !string.Equals(pending.OperationId, evt.OperationId, StringComparison.Ordinal) ||
            pending.Attempt != evt.Attempt)
        {
            return current;
        }

        var next = current.Clone();
        next.PendingHistoryInitialization = null;
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyHistoryInitializationRetryScheduled(
        NyxIdChatConversationGAgentState current,
        NyxIdChatHistoryInitializationRetryScheduledEvent evt)
    {
        var pending = current.PendingHistoryInitialization;
        if (pending is null ||
            !string.Equals(pending.OperationId, evt.OperationId, StringComparison.Ordinal) ||
            evt.Attempt <= pending.Attempt)
        {
            return current;
        }

        var next = current.Clone();
        next.PendingHistoryInitialization.Attempt = evt.Attempt;
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyHistoryDeliveryReservationDispatched(
        NyxIdChatConversationGAgentState current,
        NyxIdChatHistoryDeliveryReservationDispatchedEvent evt)
    {
        var reservation = current.HistoryDeliveryReservation;
        if (reservation is null ||
            reservation.Dispatched ||
            !string.Equals(reservation.DeliveryId, evt.DeliveryId, StringComparison.Ordinal) ||
            !string.Equals(
                reservation.SourceCommandId,
                evt.SourceCommandId,
                StringComparison.Ordinal))
        {
            return current;
        }

        var next = current.Clone();
        next.HistoryDeliveryReservation.Dispatched = true;
        next.HistoryDeliveryReservation.DispatchedAt = evt.DispatchedAt?.Clone();
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyHistoryDeliveryReservationRetryScheduled(
        NyxIdChatConversationGAgentState current,
        NyxIdChatHistoryDeliveryReservationRetryScheduledEvent evt)
    {
        var reservation = current.HistoryDeliveryReservation;
        if (reservation is null || reservation.Dispatched ||
            !string.Equals(reservation.DeliveryId, evt.DeliveryId, StringComparison.Ordinal) ||
            evt.Attempt <= reservation.Attempt)
        {
            return current;
        }

        var next = current.Clone();
        next.HistoryDeliveryReservation.Attempt = evt.Attempt;
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyHistoryTerminalDispatched(
        NyxIdChatConversationGAgentState current,
        NyxIdChatHistoryTerminalDispatchedEvent evt)
    {
        var pending = current.PendingHistoryTerminal;
        if (pending is null ||
            !string.Equals(pending.DeliveryId, evt.DeliveryId, StringComparison.Ordinal) ||
            !string.Equals(
                pending.SourceCommandId,
                evt.SourceCommandId,
                StringComparison.Ordinal) ||
            pending.Attempt != evt.Attempt)
        {
            return current;
        }

        var next = current.Clone();
        next.PendingHistoryTerminal = null;
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyHistoryTerminalRetryScheduled(
        NyxIdChatConversationGAgentState current,
        NyxIdChatHistoryTerminalRetryScheduledEvent evt)
    {
        var pending = current.PendingHistoryTerminal;
        if (pending is null ||
            !string.Equals(pending.DeliveryId, evt.DeliveryId, StringComparison.Ordinal) ||
            evt.Attempt <= pending.Attempt)
        {
            return current;
        }

        var next = current.Clone();
        next.PendingHistoryTerminal.Attempt = evt.Attempt;
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyOperationDispatched(
        NyxIdChatConversationGAgentState current,
        NyxIdChatOperationDispatchedEvent evt)
    {
        var next = current.Clone();
        var step = next.ActiveTask?.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, evt.Key));
        if (step?.Operation is null)
            return current;

        step.Operation.Phase = NyxIdChatOperationPhase.Dispatched;
        step.Operation.DispatchedAt = evt.DispatchedAt?.Clone();
        step.Operation.LastProgressAt ??= evt.DispatchedAt?.Clone();
        if (evt.EffectDispatchWaterline != NyxIdChatEffectEvidence.Unspecified)
            step.ExternalEffect = evt.EffectDispatchWaterline;
        if (KeysEqual(next.PendingOperationDeliveryProbe, evt.Key))
            next.PendingOperationDeliveryProbe = null;
        next.UpdatedAt = evt.DispatchedAt?.Clone();
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyOperationDispatchUncertain(
        NyxIdChatConversationGAgentState current,
        NyxIdChatOperationDispatchUncertainEvent evt)
    {
        if (evt.Key is null ||
            evt.State?.ActiveTask is null ||
            !string.Equals(
                evt.State.ConversationActorId,
                current.ConversationActorId,
                StringComparison.Ordinal))
        {
            return current;
        }

        var operation = evt.State.ActiveTask.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(candidate => KeysEqual(candidate?.Key, evt.Key));
        return operation?.Phase != NyxIdChatOperationPhase.Dispatched
            ? current
            : evt.State.Clone();
    }

    private static NyxIdChatConversationGAgentState ApplyOperationProgressed(
        NyxIdChatConversationGAgentState current,
        NyxIdChatOperationProgressedEvent evt)
    {
        var progress = evt.Progress;
        var next = current.Clone();
        var task = next.ActiveTask;
        if (task is null)
            return current;

        var step = task.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, progress?.Key));
        var operation = step?.Operation;
        if (step is null || operation is null ||
            progress is null ||
            evt.CommittedAt is null ||
            progress.Sequence <= operation.LatestProgressSequence ||
            evt.ProgressSequence <= current.ProgressSequence ||
            evt.StepChangeKind is not (NyxIdChatStepChangeKind.Unspecified or
                NyxIdChatStepChangeKind.Status or
                NyxIdChatStepChangeKind.Substep) ||
            evt.StepChangeKind == NyxIdChatStepChangeKind.Unspecified &&
            operation.LastStepChangedAt is null)
        {
            return current;
        }

        operation.LatestProgressSequence = progress.Sequence;
        operation.LastProgressAt = evt.CommittedAt?.Clone();
        operation.StalledAt = null;
        ApplyPhaseProgress(step, progress.Phase);
        if (evt.StepChangeKind != NyxIdChatStepChangeKind.Unspecified)
        {
            operation.LastStepChangedAt = evt.CommittedAt?.Clone();
            operation.PendingStepChangedProgressSequence = 0;
            operation.StepChangedDueAt = null;
        }
        else
        {
            operation.PendingStepChangedProgressSequence = progress.Sequence;
            operation.StepChangedDueAt ??= Timestamp.FromDateTimeOffset(
                operation.LastStepChangedAt!.ToDateTimeOffset() + OperationStepChangedCadence);
        }
        step.UpdatedAt = evt.CommittedAt?.Clone();
        task.UpdatedAt = evt.CommittedAt?.Clone();
        next.ProgressSequence = evt.ProgressSequence;
        next.UpdatedAt = evt.CommittedAt?.Clone();
        if (KeysEqual(next.PendingOperationDeliveryProbe, progress.Key))
            next.PendingOperationDeliveryProbe = null;
        return NyxIdChatNeedsYouDecisions.RefreshAttention(next);
    }

    private static NyxIdChatConversationGAgentState ApplyOperationStepChangedCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatOperationStepChangedCommittedEvent evt)
    {
        if (evt.Key is null ||
            evt.CommittedAt is null ||
            evt.GenuineProgressSequence <= 0 ||
            evt.ProgressSequence <= current.ProgressSequence)
        {
            return current;
        }

        var next = current.Clone();
        var task = next.ActiveTask;
        var step = task?.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, evt.Key));
        var operation = step?.Operation;
        if (task is null || step is null || operation is null ||
            operation.PendingStepChangedProgressSequence != evt.GenuineProgressSequence ||
            operation.LatestProgressSequence < evt.GenuineProgressSequence)
        {
            return current;
        }

        operation.LastStepChangedAt = evt.CommittedAt.Clone();
        operation.PendingStepChangedProgressSequence = 0;
        operation.StepChangedDueAt = null;
        step.UpdatedAt = evt.CommittedAt.Clone();
        task.UpdatedAt = evt.CommittedAt.Clone();
        next.ProgressSequence = evt.ProgressSequence;
        next.UpdatedAt = evt.CommittedAt.Clone();
        return NyxIdChatNeedsYouDecisions.RefreshAttention(next);
    }

    private static NyxIdChatConversationGAgentState ApplyOperationStalled(
        NyxIdChatConversationGAgentState current,
        NyxIdChatOperationStalledEvent evt)
    {
        if (evt.State?.ActiveTask is null ||
            evt.Key is null ||
            evt.ProgressSequence <= current.ProgressSequence ||
            !string.Equals(evt.State.ConversationActorId, current.ConversationActorId, StringComparison.Ordinal))
        {
            return current;
        }

        var operation = evt.State.ActiveTask.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(candidate => KeysEqual(candidate?.Key, evt.Key));
        return operation?.StalledAt is null ||
               operation.LatestProgressSequence != evt.ExpectedProgressSequence
            ? current
            : evt.State.Clone();
    }

    private static NyxIdChatConversationGAgentState ApplyOperationReconciled(
        NyxIdChatConversationGAgentState current,
        NyxIdChatOperationReconciledEvent evt)
    {
        if (evt.Result?.Key is null ||
            evt.Task is null ||
            evt.Turn is null ||
            evt.ProgressSequence <= current.ProgressSequence)
        {
            return current;
        }

        var currentStep = current.ActiveTask?.Steps
            .FirstOrDefault(candidate => KeysEqual(candidate.Operation?.Key, evt.Result.Key));
        var reconciledStep = evt.Task.Steps
            .FirstOrDefault(candidate => KeysEqual(candidate.Operation?.Key, evt.Result.Key));
        if (currentStep?.Operation is null || reconciledStep?.Operation is null)
            return current;

        if (evt.State is not null)
        {
            if (!string.Equals(
                    evt.State.ConversationActorId,
                    current.ConversationActorId,
                    StringComparison.Ordinal) ||
                !string.Equals(evt.State.ScopeId, current.ScopeId, StringComparison.Ordinal) ||
                evt.State.ProgressSequence != evt.ProgressSequence ||
                evt.State.ActiveTask is null ||
                evt.State.ActiveTurn is null ||
                !string.Equals(
                    evt.State.ActiveTask.TaskId,
                    evt.Result.Key.TaskId,
                    StringComparison.Ordinal) ||
                !OperationTurnMatchesReconciledState(
                    current,
                    evt.State,
                    currentStep,
                    evt.Result.Key))
            {
                return current;
            }

            var committed = evt.State.Clone();
            if (KeysEqual(committed.PendingOperationDeliveryProbe, evt.Result.Key))
                committed.PendingOperationDeliveryProbe = null;
            return committed;
        }

        var next = current.Clone();
        next.ActiveTask = evt.Task.Clone();
        next.ActiveTurn = evt.Turn.Clone();
        next.LatestTurn = evt.Turn.Clone();
        next.ProgressSequence = evt.ProgressSequence;
        next.UpdatedAt = evt.Turn.TerminalAt?.Clone() ?? evt.Task.UpdatedAt?.Clone();
        if (KeysEqual(next.PendingOperationDeliveryProbe, evt.Result.Key))
            next.PendingOperationDeliveryProbe = null;
        return next;
    }

    private static bool OperationTurnMatchesReconciledState(
        NyxIdChatConversationGAgentState current,
        NyxIdChatConversationGAgentState reconciled,
        NyxIdChatTaskStepState currentStep,
        NyxIdChatOperationKey key)
    {
        if (string.Equals(
                reconciled.ActiveTurn.TurnId,
                key.TurnId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return NyxIdChatActionContinuationCorrelation.TryMatch(
            current,
            reconciled.ActiveTask,
            reconciled.ActiveTurn,
            key,
            out _);
    }

    private static NyxIdChatConversationGAgentState ApplyLateOperationEvidenceCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatLateOperationEvidenceCommittedEvent evt)
    {
        if (evt.Key is null ||
            evt.State?.ActiveTask is null ||
            evt.State.ActiveTurn is null ||
            evt.ProgressSequence <= current.ProgressSequence ||
            evt.State.ProgressSequence != evt.ProgressSequence ||
            !string.Equals(
                evt.State.ConversationActorId,
                current.ConversationActorId,
                StringComparison.Ordinal) ||
            !string.Equals(evt.State.ScopeId, current.ScopeId, StringComparison.Ordinal) ||
            evt.State.ActiveTask.Status != NyxIdChatTaskStatus.Stopped ||
            evt.State.ActiveTurn.Status != NyxIdChatTurnStatus.Stopped ||
            !string.Equals(
                evt.State.ControlFence?.RequestId,
                current.ControlFence?.RequestId,
                StringComparison.Ordinal) ||
            !evt.State.ActiveTask.Steps.Any(step => KeysEqual(step.Operation?.Key, evt.Key)))
        {
            return current;
        }

        var next = evt.State.Clone();
        if (KeysEqual(next.PendingOperationDeliveryProbe, evt.Key))
            next.PendingOperationDeliveryProbe = null;
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyControlFenceCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatControlFenceCommittedEvent evt)
    {
        if (evt.State is not null)
            return evt.State.Clone();
        if (evt.Fence is null || evt.Task is null || evt.Turn is null)
            return current;

        var next = current.Clone();
        next.ControlFence = evt.Fence.Clone();
        next.LatestControlResult = evt.Fence.Clone();
        next.ActiveTask = evt.Task.Clone();
        next.ActiveTurn = evt.Turn.Clone();
        next.LatestTurn = evt.Turn.Clone();
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyContinuationAdmissionCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatContinuationAdmissionCommittedEvent evt)
    {
        if (evt.State is not null)
            return evt.State.Clone();
        if (evt.Admission is null)
            return current;

        var next = current.Clone();
        next.ContinuationAdmission = evt.Admission.Clone();
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyStepControlCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatStepControlCommittedEvent evt)
    {
        if (evt.Result is null ||
            evt.State?.ActiveTask is null ||
            evt.State.ActiveTurn is null ||
            evt.State.LatestStepControlResult is null ||
            evt.State.ProgressSequence <= current.ProgressSequence ||
            !string.Equals(
                evt.State.ConversationActorId,
                current.ConversationActorId,
                StringComparison.Ordinal) ||
            !string.Equals(evt.State.ScopeId, current.ScopeId, StringComparison.Ordinal) ||
            !evt.State.LatestStepControlResult.ToByteString()
                .Equals(evt.Result.ToByteString()))
        {
            return current;
        }

        return evt.State.Clone();
    }

    private static NyxIdChatConversationGAgentState ApplyActionRequested(
        NyxIdChatConversationGAgentState current,
        NyxIdChatActionRequestedEvent evt)
    {
        if (evt.State?.ActiveTask is null ||
            evt.State.ActiveTurn is null ||
            evt.Request is null ||
            !string.Equals(
                evt.State.ConversationActorId,
                evt.Request.ConversationActorId,
                StringComparison.Ordinal) ||
            !string.Equals(
                evt.State.ActiveTurn.TurnId,
                evt.Request.OriginTurnId,
                StringComparison.Ordinal) ||
            !evt.State.PendingActions.Any(action =>
                action.ToByteString().Equals(evt.Request.ToByteString())))
        {
            return current;
        }

        return evt.State.Clone();
    }

    private bool TryResolveCurrentOperation(
        NyxIdChatOperationKey? key,
        out NyxIdChatOperationState operation)
    {
        operation = null!;
        if (key is null || State.ActiveTask is null)
            return false;

        var candidate = State.ActiveTask.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(current => KeysEqual(current?.Key, key));
        if (candidate is null)
            return false;

        operation = candidate;
        return true;
    }

    private static NyxIdChatOperationState? ResolveOutstandingRecoveryOperation(
        NyxIdChatConversationGAgentState state)
    {
        if (state.ActiveTurn?.Status != NyxIdChatTurnStatus.Active ||
            state.ActiveTask?.Status != NyxIdChatTaskStatus.Active ||
            string.IsNullOrWhiteSpace(state.ActiveTask.ActiveOperationId))
        {
            return null;
        }

        return state.ActiveTask.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(candidate =>
                candidate?.Key is not null &&
                string.Equals(
                    candidate.Key.OperationId,
                    state.ActiveTask.ActiveOperationId,
                    StringComparison.Ordinal) &&
                candidate.Phase is NyxIdChatOperationPhase.Requested or
                    NyxIdChatOperationPhase.Dispatched or
                    NyxIdChatOperationPhase.Running);
    }

    private async Task BindAgentProfileAsync(AgentProfileSnapshot? profile)
    {
        if (profile is null)
        {
            if (State.AgentProfile is not null)
                throw new InvalidOperationException("A bound agent profile cannot be removed from a conversation.");
            return;
        }

        if (!AgentProfileSnapshotCodec.Verify(profile))
            throw new InvalidOperationException("The agent profile snapshot digest is invalid.");
        if (State.AgentProfile is null)
        {
            await PersistDomainEventAsync(
                    new AgentProfileBoundEvent { Profile = profile.Clone() },
                    CancellationToken.None);
            return;
        }

        if (!AgentProfileSnapshotCodec.ByteEquivalent(State.AgentProfile, profile))
            throw new InvalidOperationException("A conversation cannot replace its bound agent profile.");
    }

    private async Task BindContextAttachmentsAsync(ConversationContextAttachmentSet? attachments)
    {
        if (!ConversationContextAttachmentAdmission.TryNormalize(attachments, out var normalized))
            throw new InvalidOperationException("The conversation context attachment set is invalid.");
        if (!ConversationContextAttachmentAdmission.HasAttachments(normalized))
            return;
        if (State.ContextAttachments is null)
        {
            await PersistDomainEventAsync(
                new ConversationContextAttachmentsBoundEvent { Attachments = normalized },
                CancellationToken.None);
            return;
        }

        if (!ConversationContextAttachmentAdmission.ByteEquivalent(State.ContextAttachments, normalized))
            throw new InvalidOperationException("A conversation cannot replace its context attachments.");
    }

    private NyxIdChatConversationGAgentState PrepareHistoryInitializationState(string scopeId)
    {
        var next = State.Clone();
        var existingOperationId = NormalizeOptional(next.HistoryInitializationOperationId);
        var operationId = existingOperationId ??
                          BuildStableIdentity("history-initialization", Id, scopeId);
        next.HistoryInitializationOperationId = operationId;
        if (existingOperationId is null && next.PendingHistoryInitialization is null)
        {
            next.PendingHistoryInitialization = new NyxIdChatHistoryInitializationOutbox
            {
                OperationId = operationId,
                ScopeId = scopeId,
                ConversationId = Id,
                ServiceId = Id,
                ServiceKind = NyxIdChatServiceDefaults.GAgentKind,
                CreatedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                Attempt = 1,
            };
        }

        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyInputRequested(
        NyxIdChatConversationGAgentState current,
        NyxIdChatInputRequestedEvent evt) =>
        evt.State?.Clone() ?? current;

    private static NyxIdChatConversationGAgentState ApplyConversationHistoryDeleted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatConversationHistoryDeletedEvent evt)
    {
        var deletedAt = evt.DeletedAt?.Clone() ?? current.UpdatedAt?.Clone();
        var next = current.Clone();
        next.Deleted = true;
        next.DeletedAt = deletedAt;
        next.UpdatedAt = deletedAt?.Clone();
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyInputResolutionCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatInputResolutionCommittedEvent evt) =>
        evt.State?.Clone() ?? current;

    private static NyxIdChatConversationGAgentState ApplyApprovalResolutionCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatApprovalResolutionCommittedEvent evt) =>
        evt.State?.Clone() ?? current;

    private static NyxIdChatConversationGAgentState ApplyCanaryEffectFaultArmedCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatCanaryEffectFaultArmedCommittedEvent evt) =>
        evt.State?.Clone() ?? current;

    private static NyxIdChatConversationGAgentState ApplyCanaryEffectFaultConsumedCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatCanaryEffectFaultConsumedCommittedEvent evt) =>
        evt.State?.Clone() ?? current;

    private NyxIdChatHistoryDeliveryReservationState BuildHistoryDeliveryReservation(
        NyxIdChatStartTurnCommand command)
    {
        var sourceCommandId = command.CommandId.Trim();
        var prompt = command.Prompt.Trim();
        return new NyxIdChatHistoryDeliveryReservationState
        {
            DeliveryId = BuildStableIdentity(
                "chat-history-delivery",
                Id,
                command.TurnId.Trim()),
            ScopeId = command.ScopeId.Trim(),
            ConversationId = Id,
            TurnId = command.TurnId.Trim(),
            UserText = string.IsNullOrWhiteSpace(prompt) && command.InputParts.Count > 0
                ? SharedInputHistoryText
                : prompt,
            SourceActorId = Id,
            SourceCommandId = sourceCommandId,
            SourceCorrelationId = command.CorrelationId.Trim(),
            RequestFingerprint = BuildHistoryRequestFingerprint(command, sourceCommandId),
            CreateConversationIfMissing = true,
            ExposeCreateRecovery = false,
            Attempt = 1,
        };
    }

    private NyxIdChatHistoryDeliveryReservationState
        BuildActionContinuationHistoryReservation(
            NyxIdChatActionContinueCommand command,
            NyxIdChatContinuationAdmissionState admission)
    {
        var userText = BuildActionContinuationHistoryText(admission.ActionReports);
        var sourceCommandId = command.CommandId.Trim();
        var reportFingerprint = string.Join(
            "|",
            admission.ActionReports.Select(static report =>
                $"{report.ActionRequestId}:{(int)report.Disposition}"));
        return new NyxIdChatHistoryDeliveryReservationState
        {
            DeliveryId = BuildStableIdentity(
                "chat-history-delivery",
                Id,
                admission.ContinuationTurnId),
            ScopeId = command.ScopeId.Trim(),
            ConversationId = Id,
            TurnId = admission.ContinuationTurnId,
            UserText = userText,
            SourceActorId = Id,
            SourceCommandId = sourceCommandId,
            SourceCorrelationId = command.CorrelationId.Trim(),
            RequestFingerprint = BuildStableIdentity(
                "chat-history-request",
                Id,
                admission.ContinuationTurnId,
                admission.OriginTurnId,
                admission.ClientRequestId,
                sourceCommandId,
                reportFingerprint),
            CreateConversationIfMissing = true,
            ExposeCreateRecovery = false,
            Attempt = 1,
        };
    }

    private async Task ScheduleHistoryReservationRetryAsync(
        NyxIdChatHistoryDeliveryReservationState pending)
    {
        var nextAttempt = pending.Attempt == int.MaxValue
            ? int.MaxValue
            : Math.Max(1, pending.Attempt + 1);
        await PersistDomainEventAsync(new NyxIdChatHistoryDeliveryReservationRetryScheduledEvent
        {
            DeliveryId = pending.DeliveryId,
            Attempt = nextAttempt,
            FailureCode = "history_reservation_dispatch_failed",
            ScheduledAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);

        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                BuildStableIdentity(
                    "history-reservation-retry",
                    pending.DeliveryId,
                    nextAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                HistoryReservationRetryDelay,
                new NyxIdChatHistoryDeliveryReservationDispatchRequested
                {
                    DeliveryId = pending.DeliveryId,
                    Attempt = nextAttempt,
                },
                ct: CancellationToken.None);
        }
        catch (Exception schedulingException)
        {
            Logger.LogWarning(
                "NyxIdChat history reservation retry scheduling failed: actor={ActorId} delivery={DeliveryId} attempt={Attempt} exceptionType={ExceptionType}",
                Id,
                pending.DeliveryId,
                nextAttempt,
                schedulingException.GetType().Name);
        }
    }

    private static string BuildActionContinuationHistoryText(
        IEnumerable<NyxIdChatActionReport> reports)
    {
        var values = reports.ToArray();
        return values.Length == 0
            ? "NyxID state changed; recheck pending actions."
            : $"NyxID action update: {string.Join(
            ", ",
            values.Select(static report => report.Disposition switch
            {
                NyxIdChatActionDisposition.Completed => "completed",
                NyxIdChatActionDisposition.Declined => "declined",
                NyxIdChatActionDisposition.Failed => "failed",
                NyxIdChatActionDisposition.Cancelled => "cancelled",
                NyxIdChatActionDisposition.Expired => "expired",
                _ => throw new InvalidOperationException(
                    "Action continuation history requires a closed disposition."),
            }))}.";
    }

    private Task ReserveHistoryDeliveryAsync(
        NyxIdChatHistoryDeliveryReservationState reservation,
        CancellationToken ct) =>
        Services.GetRequiredService<IChatHistoryCommandPort>()
            .ReserveTurnDeliveryAsync(
                new ChatHistoryTurnDeliveryReservation(
                    reservation.DeliveryId,
                    reservation.ScopeId,
                    reservation.ConversationId,
                    reservation.TurnId,
                    reservation.UserText,
                    reservation.SourceActorId,
                    reservation.SourceCommandId,
                    reservation.SourceCorrelationId,
                    reservation.RequestFingerprint,
                    reservation.CreateConversationIfMissing,
                    reservation.ExposeCreateRecovery),
                ct);

    private bool PrepareHistoryTerminalOutbox(
        NyxIdChatConversationGAgentState state,
        string? completedText = null)
    {
        var turn = state.ActiveTurn;
        if (turn is null ||
            turn.Status is not (NyxIdChatTurnStatus.Succeeded or
                NyxIdChatTurnStatus.Failed or
                NyxIdChatTurnStatus.Stopped or
                NyxIdChatTurnStatus.Blocked))
        {
            return false;
        }

        var reservation = state.HistoryDeliveryReservation;
        if (reservation is null ||
            !string.Equals(reservation.TurnId, turn.TurnId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A terminal NyxIdChat turn requires its committed history reservation.");
        }

        var outbox = new NyxIdChatHistoryTerminalOutbox
        {
            DeliveryId = reservation.DeliveryId,
            TurnId = turn.TurnId,
            SourceActorId = reservation.SourceActorId,
            SourceCommandId = reservation.SourceCommandId,
            Status = turn.Status,
            Text = turn.Status switch
            {
                NyxIdChatTurnStatus.Succeeded => NormalizeOptional(completedText) ?? string.Empty,
                NyxIdChatTurnStatus.Failed or NyxIdChatTurnStatus.Blocked =>
                    NormalizeOptional(turn.SafeMessage) ?? string.Empty,
                _ => string.Empty,
            },
            ErrorCode = turn.Status == NyxIdChatTurnStatus.Succeeded
                ? string.Empty
                : NormalizeOptional(turn.FailureCode) ?? string.Empty,
            ObservedAt = turn.TerminalAt?.Clone() ??
                         Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Attempt = 1,
        };

        if (state.PendingHistoryTerminal is { } existing)
        {
            // The ledger snapshot is taken once, when the outbox is first prepared.
            // Retries reuse the committed snapshot so this idempotency guard keeps
            // comparing the same bytes even if the task advanced meanwhile.
            outbox.Operations.AddRange(existing.Operations.Select(operation => operation.Clone()));
            if (!existing.ToByteString().Equals(outbox.ToByteString()))
            {
                throw new InvalidOperationException(
                    "A different NyxIdChat history terminal delivery is already pending.");
            }

            return false;
        }

        outbox.Operations.AddRange(NyxIdChatOperationLedger.SnapshotTurn(state, turn.TurnId));
        state.PendingHistoryTerminal = outbox;
        return true;
    }

    private async Task ScheduleActivationHistoryInitializationAsync(
        NyxIdChatHistoryInitializationOutbox pending,
        CancellationToken ct)
    {
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                BuildStableIdentity(
                    "history-initialization-activation",
                    pending.OperationId,
                    pending.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ActivationRecoveryDelay,
                new NyxIdChatHistoryInitializationDispatchRequested
                {
                    OperationId = pending.OperationId,
                    Attempt = pending.Attempt,
                },
                new EventEnvelopePublishOptions
                {
                    Propagation = new EventEnvelopePropagationOverrides
                    {
                        CorrelationId = pending.OperationId,
                    },
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        OperationId = BuildStableIdentity(
                            "history-initialization-dispatch",
                            pending.OperationId,
                            pending.Attempt.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)),
                    },
                },
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                "NyxIdChat history initialization activation recovery scheduling failed: actor={ActorId} operation={OperationId} attempt={Attempt} exceptionType={ExceptionType}",
                Id,
                pending.OperationId,
                pending.Attempt,
                exception.GetType().Name);
            throw;
        }
    }

    private async Task ScheduleActivationHistoryReservationAsync(
        NyxIdChatHistoryDeliveryReservationState pending,
        CancellationToken ct)
    {
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                BuildStableIdentity(
                    "history-reservation-activation",
                    pending.DeliveryId,
                    pending.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ActivationRecoveryDelay,
                new NyxIdChatHistoryDeliveryReservationDispatchRequested
                {
                    DeliveryId = pending.DeliveryId,
                    Attempt = pending.Attempt,
                },
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                "NyxIdChat history reservation activation recovery scheduling failed: actor={ActorId} delivery={DeliveryId} attempt={Attempt} exceptionType={ExceptionType}",
                Id,
                pending.DeliveryId,
                pending.Attempt,
                exception.GetType().Name);
            throw;
        }
    }

    private async Task ScheduleActivationHistoryTerminalAsync(
        NyxIdChatHistoryTerminalOutbox pending,
        CancellationToken ct)
    {
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                BuildStableIdentity(
                    "history-terminal-activation",
                    pending.DeliveryId,
                    pending.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ActivationRecoveryDelay,
                new NyxIdChatHistoryTerminalDispatchRequested
                {
                    DeliveryId = pending.DeliveryId,
                    Attempt = pending.Attempt,
                },
                new EventEnvelopePublishOptions
                {
                    Propagation = new EventEnvelopePropagationOverrides
                    {
                        CorrelationId = pending.SourceCommandId,
                    },
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        OperationId = BuildStableIdentity(
                            "history-terminal-dispatch",
                            pending.DeliveryId,
                            pending.Attempt.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)),
                    },
                },
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                "NyxIdChat history terminal activation recovery scheduling failed: actor={ActorId} delivery={DeliveryId} attempt={Attempt} exceptionType={ExceptionType}",
                Id,
                pending.DeliveryId,
                pending.Attempt,
                exception.GetType().Name);
            throw;
        }
    }

    private Task ScheduleOutstandingOperationRecoveryAsync(CancellationToken ct)
    {
        if (HasPendingOperationRecoveryBarrier(State))
            return Task.CompletedTask;

        var operation = ResolveOutstandingRecoveryOperation(State);
        return operation?.Key is null
            ? Task.CompletedTask
            : ScheduleActivationRecoveryAsync(operation, ct);
    }

    private static bool HasPendingOperationRecoveryBarrier(
        NyxIdChatConversationGAgentState state) =>
        state.PendingOperationDeliveryProbe is not null;

    private Task ScheduleOutstandingOperationStepChangedAsync(CancellationToken ct)
    {
        var operation = State.ActiveTask?.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(static candidate =>
                candidate?.Key is not null &&
                candidate.PendingStepChangedProgressSequence > 0 &&
                candidate.StepChangedDueAt is not null &&
                IsInFlight(candidate.Phase));
        return operation is null
            ? Task.CompletedTask
            : ScheduleOperationStepChangedAsync(operation, ct);
    }

    private async Task ScheduleOperationStepChangedAsync(
        NyxIdChatOperationState operation,
        CancellationToken ct)
    {
        if (operation.Key is null || operation.StepChangedDueAt is null)
            return;

        var delay = operation.StepChangedDueAt.ToDateTimeOffset() - _timeProvider.GetUtcNow();
        var signal = new NyxIdChatOperationStepChangedDueSignal
        {
            Key = operation.Key.Clone(),
            ExpectedDueAt = operation.StepChangedDueAt.Clone(),
        };
        var options = new EventEnvelopePublishOptions
        {
            Propagation = new EventEnvelopePropagationOverrides
            {
                CorrelationId = operation.Key.OperationId,
            },
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = BuildStableIdentity(
                    "operation-step-changed",
                    operation.Key.OperationId,
                    operation.Key.OperationGeneration.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    operation.StepChangedDueAt.ToDateTimeOffset().ToUnixTimeMilliseconds().ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
            },
        };
        if (delay <= TimeSpan.Zero)
        {
            await PublishAsync(signal, TopologyAudience.Self, ct, options);
            return;
        }

        await ScheduleSelfDurableTimeoutAsync(
            options.Delivery.OperationId,
            delay,
            signal,
            options,
            ct);
    }

    private Task ScheduleOutstandingOperationStallCheckAsync(CancellationToken ct)
    {
        var operation = State.ActiveTask?.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(static candidate =>
                candidate?.Key is not null &&
                candidate.LastProgressAt is not null &&
                candidate.StalledAt is null &&
                IsInFlight(candidate.Phase));
        return operation is null
            ? Task.CompletedTask
            : ScheduleOperationStallCheckAsync(operation, ct);
    }

    private async Task ScheduleOperationStallCheckAsync(
        NyxIdChatOperationState operation,
        CancellationToken ct)
    {
        if (operation.Key is null || operation.LastProgressAt is null)
            return;

        var dueAt = operation.LastProgressAt.ToDateTimeOffset() + OperationStallThreshold;
        var delay = dueAt - _timeProvider.GetUtcNow();
        var signal = new NyxIdChatOperationStallCheckSignal
        {
            Key = operation.Key.Clone(),
            ExpectedProgressSequence = operation.LatestProgressSequence,
            ExpectedLastProgressAt = operation.LastProgressAt.Clone(),
        };
        var options = new EventEnvelopePublishOptions
        {
            Propagation = new EventEnvelopePropagationOverrides
            {
                CorrelationId = operation.Key.OperationId,
            },
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = BuildStableIdentity(
                    "operation-stall-check",
                    operation.Key.OperationId,
                    operation.Key.OperationGeneration.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    operation.LatestProgressSequence.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    operation.LastProgressAt.ToDateTimeOffset().ToUnixTimeMilliseconds().ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
            },
        };
        if (delay <= TimeSpan.Zero)
        {
            await PublishAsync(signal, TopologyAudience.Self, ct, options);
            return;
        }

        await ScheduleSelfDurableTimeoutAsync(
            options.Delivery.OperationId,
            delay,
            signal,
            options,
            ct);
    }

    private async Task ScheduleActivationRecoveryAsync(
        NyxIdChatOperationState operation,
        CancellationToken ct)
    {
        var version = CurrentCommittedVersion();
        var kind = operation.Kind == NyxIdChatStepKind.Postcondition &&
                   operation.Phase == NyxIdChatOperationPhase.Requested
            ? NyxIdChatRecoveryKind.PostconditionRedispatch
            : NyxIdChatRecoveryKind.InterruptedOperationReconciliation;
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                BuildStableIdentity(
                    "operation-recovery-activation",
                    operation.Key.OperationId,
                    version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ActivationRecoveryDelay,
                new NyxIdChatRecoveryRequestedSignal
                {
                    Key = operation.Key.Clone(),
                    ExpectedStateVersion = version,
                    Kind = kind,
                },
                new EventEnvelopePublishOptions
                {
                    Propagation = new EventEnvelopePropagationOverrides
                    {
                        CorrelationId = operation.Key.OperationId,
                    },
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        OperationId = $"{operation.Key.OperationId}:recovery:{version}",
                    },
                },
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                "NyxIdChat operation activation recovery scheduling failed: actor={ActorId} operation={OperationId} version={StateVersion} exceptionType={ExceptionType}",
                Id,
                operation.Key.OperationId,
                version,
                exception.GetType().Name);
            throw;
        }
    }

    private Task DispatchPendingHistoryTerminalAsync()
    {
        var pending = State.PendingHistoryTerminal;
        return pending is null || State.HistoryDeliveryReservation?.Dispatched != true
            ? Task.CompletedTask
            : DispatchHistoryTerminalContinuationAsync(pending, CancellationToken.None);
    }

    private Task DispatchHistoryTerminalContinuationAsync(
        NyxIdChatHistoryTerminalOutbox pending,
        CancellationToken ct)
    {
        var signal = new NyxIdChatHistoryTerminalDispatchRequested
        {
            DeliveryId = pending.DeliveryId,
            Attempt = pending.Attempt,
        };
        return PublishAsync(
            signal,
            TopologyAudience.Self,
            ct,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    CorrelationId = pending.SourceCommandId,
                },
                Delivery = new EventEnvelopeDeliveryOptions
                {
                    OperationId = BuildStableIdentity(
                        "history-terminal-dispatch",
                        pending.DeliveryId,
                        pending.Attempt.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)),
                },
            });
    }

    private static ChatHistoryTurnTerminalStatus ToHistoryTerminalStatus(
        NyxIdChatTurnStatus status) => status switch
        {
            NyxIdChatTurnStatus.Succeeded => ChatHistoryTurnTerminalStatus.Completed,
            NyxIdChatTurnStatus.Failed => ChatHistoryTurnTerminalStatus.Failed,
            NyxIdChatTurnStatus.Stopped => ChatHistoryTurnTerminalStatus.Stopped,
            NyxIdChatTurnStatus.Blocked => ChatHistoryTurnTerminalStatus.Blocked,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    private static ChatHistoryTurnOperation ToHistoryTurnOperation(
        NyxIdChatTurnOperationSnapshot snapshot)
    {
        var facts = snapshot.LedgerFacts;
        return new ChatHistoryTurnOperation(
            OperationId: snapshot.OperationId,
            Order: snapshot.Order,
            Kind: snapshot.Kind == NyxIdChatStepKind.Llm
                ? ChatHistoryTurnOperationKind.Model
                : ChatHistoryTurnOperationKind.Tool,
            Title: snapshot.Title,
            Status: ToHistoryOperationStatus(snapshot.Status),
            StartedAt: snapshot.StartedAt?.ToDateTimeOffset(),
            CompletedAt: snapshot.CompletedAt?.ToDateTimeOffset(),
            Model: NormalizeOptional(facts?.Model),
            Provider: NormalizeOptional(facts?.Provider),
            FinishReason: NormalizeOptional(facts?.FinishReason),
            PromptTokens: facts?.Usage?.PromptTokens ?? 0,
            CompletionTokens: facts?.Usage?.CompletionTokens ?? 0,
            TotalTokens: facts?.Usage?.TotalTokens ?? 0,
            InputPreview: NormalizeOptional(facts?.InputPreview),
            OutputPreview: NormalizeOptional(facts?.OutputPreview),
            ArgumentsPreview: NormalizeOptional(facts?.ArgumentsPreview),
            PreviewsTruncated: facts?.PreviewsTruncated ?? false,
            SafeMessage: NormalizeOptional(snapshot.SafeMessage) ??
                         NormalizeOptional(snapshot.TerminalCode),
            AvailableToolNames: facts?.AvailableToolNames.ToArray() ?? [],
            ToolCatalogCaptured: facts?.ToolCatalogCaptured ?? false);
    }

    private static string ToHistoryOperationStatus(NyxIdChatStepStatus status) => status switch
    {
        NyxIdChatStepStatus.Done => "done",
        NyxIdChatStepStatus.Failed => "error",
        NyxIdChatStepStatus.Cancelled => "stopped",
        NyxIdChatStepStatus.Skipped => "skipped",
        NyxIdChatStepStatus.Uncertain => "uncertain",
        NyxIdChatStepStatus.Running => "running",
        NyxIdChatStepStatus.Waiting => "waiting",
        _ => "closed",
    };

    private async Task PersistOperationDispatchFailureAsync(
        NyxIdChatOperationKey operationKey,
        string failureCode,
        string safeMessage,
        Exception? exception = null)
    {
        if (exception is not null)
        {
            Logger.LogWarning(
                "NyxIdChat operation dispatch stage failed: actor={ActorId} operation={OperationId} code={FailureCode} exceptionType={ExceptionType}",
                Id,
                operationKey.OperationId,
                failureCode,
                exception.GetType().Name);
        }

        var state = State.Clone();
        var step = state.ActiveTask?.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, operationKey));
        if (step is null)
            return;

        // The transient command carries the only runtime execution capability.
        // Once dispatch fails, committed state cannot reconstruct it, so close
        // the operation instead of leaving a running step with no continuation.
        step.RetryInputRebuildable = false;
        var failure = new NyxIdChatOperationResultSignal
        {
            Key = operationKey.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = failureCode,
                SafeMessage = safeMessage,
                ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            },
        };
        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(
            state,
            failure,
            Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        if (decision.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return;

        var next = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        if (KeysEqual(next.PendingOperationDeliveryProbe, operationKey))
            next.PendingOperationDeliveryProbe = null;
        next.ProgressSequence = State.ProgressSequence + 1;
        next.UpdatedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var terminalPrepared = PrepareHistoryTerminalOutbox(next);
        await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
        {
            Result = failure,
            Task = next.ActiveTask.Clone(),
            Turn = next.ActiveTurn.Clone(),
            ProgressSequence = next.ProgressSequence,
            State = next,
        }, CancellationToken.None);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();
    }

    private async Task PersistAmbiguousOperationDispatchAsync(
        NyxIdChatOperationDispatchCommand command,
        Exception exception)
    {
        var operationKey = command.Key;
        Logger.LogWarning(
            "NyxIdChat operation delivery is uncertain: actor={ActorId} operation={OperationId} exceptionType={ExceptionType}",
            Id,
            operationKey.OperationId,
            exception.GetType().Name);

        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var next = State.Clone();
        var step = next.ActiveTask?.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, operationKey));
        if (step?.Operation is null || !IsInFlight(step.Operation.Phase))
            return;

        var mayChangeExternalState =
            NyxIdChatTurnOperationDispatchPort.MayDispatchExternalEffect(command);
        step.Status = NyxIdChatStepStatus.Running;
        step.ExternalEffect = mayChangeExternalState
            ? NyxIdChatEffectEvidence.MayHaveChanged
            : NyxIdChatEffectEvidence.NotApplied;
        step.Operation.Phase = NyxIdChatOperationPhase.Dispatched;
        step.Operation.DispatchedAt = now.Clone();
        step.Operation.LastProgressAt ??= now.Clone();
        step.UpdatedAt = now.Clone();
        next.ActiveTask!.UpdatedAt = now.Clone();
        next.UpdatedAt = now.Clone();
        next.PendingOperationDeliveryProbe = operationKey.Clone();

        await PersistDomainEventAsync(new NyxIdChatOperationDispatchUncertainEvent
        {
            Key = operationKey.Clone(),
            ObservedAt = now,
            State = next,
        }, CancellationToken.None);

        await DispatchPendingOperationDeliveryProbeAsync(CancellationToken.None);
    }

    private Task DispatchHistoryInitializationContinuationAsync(
        NyxIdChatHistoryInitializationOutbox pending,
        CancellationToken ct)
    {
        var signal = new NyxIdChatHistoryInitializationDispatchRequested
        {
            OperationId = pending.OperationId,
            Attempt = pending.Attempt,
        };
        return PublishAsync(
            signal,
            TopologyAudience.Self,
            ct,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    CorrelationId = pending.OperationId,
                },
                Delivery = new EventEnvelopeDeliveryOptions
                {
                    OperationId = BuildStableIdentity(
                        "history-initialization-dispatch",
                        pending.OperationId,
                        pending.Attempt.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)),
                },
            });
    }

    private Task DispatchInputRequestContinuationAsync(
        NyxIdChatInputRequestCommand command,
        CancellationToken ct)
        => SendToAsync(
            Id,
            command,
            ct,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    CorrelationId = command.RequestId,
                },
                Delivery = new EventEnvelopeDeliveryOptions
                {
                    OperationId = $"{command.RequestId}:materialize",
                },
            });

    private async Task PersistRegistrationUnavailableAndCompensateAsync(
        string scopeId,
        bool destroyActor,
        string reason,
        string commandId,
        string correlationId)
    {
        await PersistDomainEventAsync(new NyxIdChatConversationRegistrationUnavailableEvent
        {
            ScopeId = scopeId,
            ActorId = Id,
            DestroyActor = destroyActor,
            Reason = reason,
            CommandId = commandId,
            CorrelationId = correlationId,
        }, CancellationToken.None);
        await HandleCreationCompensationAsync(new NyxIdChatConversationCreationCompensationRequested
        {
            ScopeId = scopeId,
            ActorId = Id,
            DestroyActor = destroyActor,
            Reason = reason,
        });
    }

    private Task<DispatchAdmission> DispatchSteeringContinuationAsync(
        NyxIdChatSteeringCommand command,
        NyxIdChatContinuationAdmissionState admission)
        => DispatchStartTurnContinuationAsync(
            BuildSteeringContinuationCommand(command, admission));

    private NyxIdChatStartTurnCommand BuildSteeringContinuationCommand(
        NyxIdChatSteeringCommand command,
        NyxIdChatContinuationAdmissionState admission)
    {
        var taskId = string.IsNullOrWhiteSpace(State.ActiveTask?.TaskId)
            ? BuildStableIdentity(
                "task",
                Id,
                admission.OriginTurnId,
                admission.RequestId)
            : State.ActiveTask.TaskId;
        var continuationCommandId = BuildStableIdentity(
            "command",
            Id,
            admission.ContinuationTurnId,
            admission.RequestId,
            "steering-continuation");
        var start = new NyxIdChatStartTurnCommand
        {
            ScopeId = command.ScopeId.Trim(),
            ConversationActorId = Id,
            TurnId = admission.ContinuationTurnId,
            TaskId = taskId,
            PlanId = State.ActiveTask?.PlanId ?? taskId,
            PlanRevision = Math.Max(1, (State.ActiveTask?.PlanRevision ?? 0) + 1),
            AddedBy = NyxIdChatStepAddedBy.Steering,
            ClientRequestId = command.ClientRequestId.Trim(),
            CommandId = continuationCommandId,
            CorrelationId = command.CorrelationId.Trim(),
            Prompt = command.Instruction.Trim(),
            SteeringExecutionContext = BuildSteeringExecutionContext(admission),
            ToolContext = command.ToolContext?.Clone(),
            LlmControl = command.LlmControl?.Clone(),
        };
        start.InputParts.AddRange(command.InputParts.Select(static part => part.Clone()));
        return start;
    }

    private NyxIdChatSteeringExecutionContext BuildSteeringExecutionContext(
        NyxIdChatContinuationAdmissionState admission)
    {
        var task = State.ActiveTask;
        var context = new NyxIdChatSteeringExecutionContext
        {
            OriginTurnId = admission.OriginTurnId,
            OriginPrompt = State.ActiveTurn?.Prompt ?? string.Empty,
            TaskId = task?.TaskId ?? string.Empty,
            PlanId = task?.PlanId ?? string.Empty,
            PlanRevision = task?.PlanRevision ?? 0,
            TaskTitle = task?.Title ?? string.Empty,
        };
        if (task is null)
            return context;

        var resolvedInputRequestIds = task.Steps
            .Where(static step =>
                step.Source?.SourceCase == NyxIdChatStepSource.SourceOneofCase.Input)
            .Select(static step => step.Source.Input.RequestId)
            .Where(static requestId => !string.IsNullOrWhiteSpace(requestId))
            .ToHashSet(StringComparer.Ordinal);
        context.InputResolutions.AddRange(State.RecentInputResolutions
            .Where(resolution => resolvedInputRequestIds.Contains(resolution.RequestId))
            .Select(static resolution => new NyxIdChatSteeringInputResolutionFact
            {
                RequestId = resolution.RequestId,
                Outcome = resolution.Outcome,
                NumericThreshold = resolution.NumericThreshold?.Clone(),
                Answer = resolution.Answer?.Clone(),
            }));
        context.CompletedSteps.AddRange(task.Steps
            .Where(static step =>
                step.Status is NyxIdChatStepStatus.Done or NyxIdChatStepStatus.Skipped)
            .OrderBy(static step => step.Order)
            .Select(static step => new NyxIdChatSteeringCompletedStepFact
            {
                StepId = step.StepId,
                Order = step.Order,
                Kind = step.Kind,
                Status = step.Status,
                Description = step.Description,
                Source = step.Source?.Clone(),
                ExternalEffect = step.ExternalEffect,
                OperationPhase = step.Operation?.Phase ?? NyxIdChatOperationPhase.Unspecified,
                TerminalCode = step.Operation?.TerminalCode ?? string.Empty,
                SafeMessage = step.Operation?.SafeMessage ?? step.SafeMessage ?? string.Empty,
                Substeps = { step.Substeps.Select(static substep => substep.Clone()) },
            }));
        return context;
    }

    private static string BuildExecutionPrompt(NyxIdChatStartTurnCommand command)
    {
        var context = command.SteeringExecutionContext;
        if (context is null)
            return command.Prompt ?? string.Empty;

        var lines = new List<string>
        {
            "Continue the same committed task using the steering instruction below.",
            "Do not ask the user to restate the original task.",
            "Do not repeat completed steps unless the steering instruction explicitly requires fresh execution.",
            "Treat completed-step facts as execution evidence, not as the raw provider response.",
            "If a provider-result detail is absent, say it cannot be checked instead of inventing it.",
            $"Steering instruction: {command.Prompt?.Trim()}",
            $"Original task: {context.OriginPrompt.Trim()}",
            $"Committed task: {context.TaskId}; plan: {context.PlanId}; revision: {context.PlanRevision}",
        };
        if (!string.IsNullOrWhiteSpace(context.TaskTitle))
            lines.Add($"Committed task title: {context.TaskTitle.Trim()}");

        foreach (var resolution in context.InputResolutions)
        {
            var threshold = resolution.NumericThreshold is null
                ? string.Empty
                : $"; numeric threshold: {resolution.NumericThreshold}";
            var answer = resolution.Answer?.AnswerCase switch
            {
                NyxIdChatInputAnswer.AnswerOneofCase.FreeText =>
                    $"; answer: free text {JsonSerializer.Serialize(resolution.Answer.FreeText)}",
                NyxIdChatInputAnswer.AnswerOneofCase.Selection =>
                    $"; answer: selected option ids " +
                    JsonSerializer.Serialize(resolution.Answer.Selection.OptionIds.ToArray()),
                _ => string.Empty,
            };
            lines.Add(
                $"Committed input resolution: {resolution.RequestId}; outcome: {resolution.Outcome}{answer}{threshold}");
        }

        foreach (var step in context.CompletedSteps.OrderBy(static fact => fact.Order))
        {
            var source = step.Source?.SourceCase switch
            {
                NyxIdChatStepSource.SourceOneofCase.Tool =>
                    $"tool {step.Source.Tool.ToolName}",
                NyxIdChatStepSource.SourceOneofCase.Postcondition =>
                    $"postcondition {step.Source.Postcondition.Check}",
                NyxIdChatStepSource.SourceOneofCase.Input =>
                    $"input {step.Source.Input.RequestId}",
                NyxIdChatStepSource.SourceOneofCase.Web =>
                    "web",
                _ => step.Kind.ToString(),
            };
            lines.Add(
                $"Completed step {step.Order}: {step.Description.Trim()} " +
                $"[{source}; status: {step.Status}; operation: {step.OperationPhase}; effect: {step.ExternalEffect}]");
            foreach (var substep in step.Substeps)
            {
                lines.Add(
                    $"Completed substep: {substep.Title.Trim()} [status: {substep.Status}]");
            }
        }

        return string.Join('\n', lines);
    }

    private Task<DispatchAdmission> DispatchStartTurnContinuationAsync(
        NyxIdChatStartTurnCommand start)
    {
        var envelope = new EventEnvelope
        {
            Id = start.CommandId,
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(start),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = Id },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = start.CorrelationId,
            },
        };
        return _actorDispatchPort.DispatchAsync(Id, envelope, CancellationToken.None);
    }

    private bool MatchesPendingSteeringContinuation(
        NyxIdChatStartTurnCommand command,
        NyxIdChatContinuationAdmissionState admission)
    {
        var expectedCommandId = BuildStableIdentity(
            "command",
            Id,
            admission.ContinuationTurnId,
            admission.RequestId,
            "steering-continuation");
        return string.Equals(command.ScopeId, State.ScopeId, StringComparison.Ordinal) &&
               string.Equals(command.ConversationActorId, Id, StringComparison.Ordinal) &&
               string.Equals(command.TurnId, State.PendingSteeringContinuationId,
                   StringComparison.Ordinal) &&
               string.Equals(command.TurnId, admission.ContinuationTurnId,
                   StringComparison.Ordinal) &&
               string.Equals(command.ClientRequestId, admission.ClientRequestId,
                   StringComparison.Ordinal) &&
               string.Equals(command.CommandId, expectedCommandId, StringComparison.Ordinal) &&
               string.Equals(command.Prompt, admission.Instruction, StringComparison.Ordinal) &&
               OwnerMatches(
                   State.OwnerSubject,
                   command.ToolContext?.Caller?.OwnerSubject) &&
               admission.InputParts.Select(static part => part.ToByteString())
                   .SequenceEqual(command.InputParts.Select(SanitizeInputPart)
                       .Select(static part => part.ToByteString()));
    }

    private async Task DispatchAuthorizedOperationAsync(
        NyxIdChatOperationDispatchCommand command,
        string correlationId,
        Timestamp now)
    {
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(Id, command.Key.TurnId);
        var envelope = new EventEnvelope
        {
            Id = command.Key.OperationId,
            Timestamp = now.Clone(),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = turnActorId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = NormalizeOptional(correlationId) ?? command.Key.OperationId,
            },
        };
        try
        {
            var admission = await _actorDispatchPort
                .DispatchAsync(turnActorId, envelope, CancellationToken.None);
            if (!admission.Accepted)
            {
                await PersistOperationDispatchFailureAsync(
                    command.Key,
                    "NYXID_CHAT_OPERATION_DISPATCH_REJECTED",
                    "The chat operation was not accepted for dispatch.");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistAmbiguousOperationDispatchAsync(command, exception);
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatOperationDispatchedEvent
        {
            Key = command.Key.Clone(),
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
        await ScheduleOutstandingOperationStallCheckAsync(CancellationToken.None);
    }

    private async Task DispatchPendingOperationDeliveryProbeAsync(CancellationToken ct)
    {
        var key = State.PendingOperationDeliveryProbe?.Clone();
        if (key is null)
            return;

        var turnActorId = NyxIdChatTurnActorIds.ForTurn(Id, key.TurnId);
        var envelope = new EventEnvelope
        {
            Id = $"{key.OperationId}:turn-operation-delivery-probe",
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(new NyxIdChatTurnOperationDeliveryProbeCommand
            {
                Key = key.Clone(),
            }),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = turnActorId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = key.OperationId,
            },
        };
        try
        {
            var dispatch = await _actorDispatchPort.DispatchAsync(turnActorId, envelope, ct);
            if (!dispatch.Accepted)
            {
                Logger.LogWarning(
                    "NyxIdChat operation delivery probe was not accepted: actor={ActorId} operation={OperationId}",
                    Id,
                    key.OperationId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat operation delivery probe failed: actor={ActorId} operation={OperationId}",
                Id,
                key.OperationId);
        }

        if (KeysEqual(State.PendingOperationDeliveryProbe, key))
        {
            await ScheduleOperationDeliveryProbeAsync(
                OperationDeliveryProbeRetryDelay,
                CancellationToken.None);
        }
    }

    private Task ScheduleOperationDeliveryProbeAsync(TimeSpan delay, CancellationToken ct)
    {
        var key = State.PendingOperationDeliveryProbe;
        if (key is null)
            return Task.CompletedTask;

        var retryAt = _timeProvider.GetUtcNow() + delay;
        return ScheduleSelfDurableTimeoutAsync(
            BuildStableIdentity(
                "operation-delivery-probe",
                Id,
                key.OperationId,
                retryAt.ToUnixTimeMilliseconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
            delay,
            new NyxIdChatOperationDeliveryProbeDispatchRequested
            {
                Key = key.Clone(),
                ExpectedStateVersion = CurrentCommittedVersion(),
            },
            ct: ct);
    }

    private static NyxIdChatOperationState? ResolvePhysicallyInFlightOperation(
        NyxIdChatConversationGAgentState state) =>
        state.ActiveTask?.Steps
            .Select(static step => step.Operation)
            .SingleOrDefault(operation =>
                operation?.Phase is NyxIdChatOperationPhase.Dispatched or
                    NyxIdChatOperationPhase.Running);

    private Task DispatchOperationCancellationAsync(NyxIdChatOperationKey key)
    {
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(Id, key.TurnId);
        var envelope = new EventEnvelope
        {
            Id = $"{key.OperationId}:cancel",
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(new NyxIdChatTurnOperationCancelCommand
            {
                Key = key.Clone(),
            }),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = turnActorId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ??
                                key.OperationId,
            },
        };
        return _actorDispatchPort.DispatchAsync(turnActorId, envelope, CancellationToken.None);
    }

    private async Task DispatchFirstOperationAsync(
        NyxIdChatOperationDispatchCommand command,
        string correlationId,
        Timestamp now)
    {
        var operationKey = command.Key;
        IActor turnActor;
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(Id, operationKey.TurnId);
        try
        {
            turnActor = await _actorRuntime
                .CreateAsync<NyxIdChatTurnGAgent>(turnActorId, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistOperationDispatchFailureAsync(
                    operationKey,
                    "NYXID_CHAT_TURN_ACTOR_CREATE_FAILED",
                    "The chat turn could not start its execution actor.",
                    exception);
            return;
        }

        try
        {
            await _actorRuntime.LinkAsync(Id, turnActor.Id, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistOperationDispatchFailureAsync(
                    operationKey,
                    "NYXID_CHAT_TURN_ACTOR_LINK_FAILED",
                    "The chat turn could not attach its execution actor.",
                    exception);
            return;
        }

        var envelope = new EventEnvelope
        {
            Id = operationKey.OperationId,
            Timestamp = now.Clone(),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = turnActor.Id },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = NormalizeOptional(correlationId) ?? operationKey.OperationId,
            },
        };
        try
        {
            var admission = await _actorDispatchPort
                .DispatchAsync(turnActor.Id, envelope, CancellationToken.None);
            if (!admission.Accepted)
            {
                await PersistOperationDispatchFailureAsync(
                        operationKey,
                        "NYXID_CHAT_OPERATION_DISPATCH_REJECTED",
                        "The chat operation was not accepted for dispatch.");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistAmbiguousOperationDispatchAsync(command, exception);
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatOperationDispatchedEvent
        {
            Key = operationKey.Clone(),
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
        await ScheduleOutstandingOperationStallCheckAsync(CancellationToken.None);
    }

    private long CurrentCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException(
            "Event sourcing must be configured before evaluating a control command."))
        .CurrentVersion;

    private void ValidateStartCommand(NyxIdChatStartTurnCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ScopeId) ||
            string.IsNullOrWhiteSpace(command.ConversationActorId) ||
            string.IsNullOrWhiteSpace(command.TurnId) ||
            string.IsNullOrWhiteSpace(command.TaskId) ||
            string.IsNullOrWhiteSpace(command.CommandId) ||
            string.IsNullOrWhiteSpace(command.CorrelationId) ||
            !string.Equals(command.ConversationActorId.Trim(), Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The NyxIdChat start command identity is incomplete or mismatched.", nameof(command));
        }
    }

    private bool SameTurnAdmission(
        NyxIdChatConversationGAgentState state,
        NyxIdChatStartTurnCommand command) =>
        string.Equals(state.ConversationActorId, command.ConversationActorId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ScopeId, command.ScopeId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.TurnId, command.TurnId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.TaskId, command.TaskId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.ClientRequestId, command.ClientRequestId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.CommandId, command.CommandId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.Prompt, command.Prompt, StringComparison.Ordinal) &&
        string.Equals(
            state.HistoryDeliveryReservation?.RequestFingerprint,
            BuildHistoryRequestFingerprint(
                command,
                state.HistoryDeliveryReservation?.SourceCommandId ?? command.CommandId.Trim()),
            StringComparison.Ordinal);

    private static bool SameTurnIdentity(
        NyxIdChatConversationGAgentState state,
        NyxIdChatStartTurnCommand command) =>
        string.Equals(state.ConversationActorId, command.ConversationActorId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ScopeId, command.ScopeId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.TurnId, command.TurnId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.TaskId, command.TaskId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.ClientRequestId, command.ClientRequestId.Trim(), StringComparison.Ordinal);

    private Task PersistTurnAdmissionRejectionAsync(
        NyxIdChatStartTurnCommand command,
        string reasonCode,
        string safeMessage) =>
        PersistDomainEventAsync(new NyxIdChatTurnAdmissionRejectedEvent
        {
            ConversationActorId = Id,
            RequestedTurnId = command.TurnId.Trim(),
            ActiveTurnId = State.ActiveTurn?.TurnId ?? string.Empty,
            CommandId = command.CommandId.Trim(),
            CorrelationId = command.CorrelationId.Trim(),
            ReasonCode = reasonCode,
            SafeMessage = safeMessage,
        }, CancellationToken.None);

    private Task PersistActionContinuationRejectionAsync(
        NyxIdChatActionContinueCommand command,
        string reasonCode,
        string safeMessage) =>
        PersistDomainEventAsync(new NyxIdChatTurnAdmissionRejectedEvent
        {
            ConversationActorId = Id,
            RequestedTurnId = command.ContinuationTurnId.Trim(),
            ActiveTurnId = State.ActiveTurn?.TurnId ?? string.Empty,
            CommandId = command.CommandId.Trim(),
            CorrelationId = command.CorrelationId.Trim(),
            ReasonCode = reasonCode,
            SafeMessage = safeMessage,
        }, CancellationToken.None);

    private string BuildHistoryRequestFingerprint(
        NyxIdChatStartTurnCommand command,
        string sourceCommandId) =>
        BuildStableIdentity(
            "chat-history-request",
            Id,
            command.TurnId.Trim(),
            command.TaskId.Trim(),
            command.ClientRequestId.Trim(),
            sourceCommandId,
            command.Prompt,
            NormalizeOptional(command.InputPartsFingerprint) ??
            BuildInputPartsFingerprint(command.InputParts));

    private static string BuildInputPartsFingerprint(
        IEnumerable<Aevatar.AI.Abstractions.ChatContentPart> inputParts) =>
        BuildStableIdentity(
            "input-parts",
            inputParts
                .Select(static part => Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(part.ToByteArray())))
                .ToArray());

    private async Task DispatchOperationResultAcknowledgementAsync(
        NyxIdChatOperationResultSignal result,
        CancellationToken ct)
    {
        if (result.Key is null ||
            (!IsCredentialFreePostconditionTerminal(result) &&
             !IsVerifiedAuthorizationContinuationAuthorizationRequired(State, result) &&
             !HasResultAcknowledgementFence(State, result)))
        {
            return;
        }

        var key = result.Key;
        var digest = ComputeResultDigest(result);
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(Id, key.TurnId);
        var generation = key.OperationGeneration.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var envelope = new EventEnvelope
        {
            Id = $"{key.OperationId}:result-ack:{generation}:{Convert.ToHexStringLower(digest)[..16]}",
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(new NyxIdChatTurnOperationResultAcknowledgedSignal
            {
                Key = key.Clone(),
                ResultSha256 = ByteString.CopyFrom(digest),
            }),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = turnActorId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ??
                                key.OperationId,
            },
        };
        await _actorDispatchPort.DispatchAsync(turnActorId, envelope, ct);
    }

    private async Task CommitFencedPostconditionResultConsumptionAsync(
        NyxIdChatOperationResultSignal result,
        NyxIdChatOperationState currentOperation,
        Timestamp committedAt)
    {
        var failure = BuildFencedPostconditionFailure(result);
        var next = State.Clone();
        next.ProgressSequence = checked(State.ProgressSequence + 1);
        next.UpdatedAt = committedAt.Clone();
        RememberResultAcknowledgementFence(next, result);
        await PersistDomainEventAsync(new NyxIdChatLateOperationEvidenceCommittedEvent
        {
            Key = result.Key.Clone(),
            OperationPhase = currentOperation.Phase,
            ExternalEffect = failure.ExternalEffect,
            TerminalCode = failure.FailureCode,
            SafeMessage = failure.SafeMessage,
            ProgressSequence = next.ProgressSequence,
            CommittedAt = committedAt.Clone(),
            State = next,
            ConsumedPostconditionFailure = failure,
        }, CancellationToken.None);
        await DispatchOperationResultAcknowledgementAsync(result, CancellationToken.None);
    }

    private static void RememberResultAcknowledgementFence(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal result)
    {
        if (!RequiresResultAcknowledgement(state, result))
            return;

        var digest = ComputeResultDigest(result);
        if (state.ResultAcknowledgementFences.Any(fence =>
                KeysEqual(fence.Key, result.Key) &&
                CryptographicOperations.FixedTimeEquals(
                    fence.ResultSha256.Span,
                    digest)))
        {
            return;
        }

        state.ResultAcknowledgementFences.Add(
            new NyxIdChatOperationResultAcknowledgementFence
            {
                Key = result.Key.Clone(),
                ResultSha256 = ByteString.CopyFrom(digest),
            });
    }

    private static bool HasResultAcknowledgementFence(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal result)
    {
        if (result.Key is null)
            return false;

        var digest = ComputeResultDigest(result);
        return state.ResultAcknowledgementFences.Any(fence =>
            KeysEqual(fence.Key, result.Key) &&
            CryptographicOperations.FixedTimeEquals(
                fence.ResultSha256.Span,
                digest));
    }

    private static bool RequiresResultAcknowledgement(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal result)
    {
        if (result.Key is null)
            return false;

        if (IsCredentialFreePostconditionTerminal(result) &&
            state.ActiveTask?.Steps.Any(step =>
                step.Kind == NyxIdChatStepKind.Postcondition &&
                KeysEqual(step.Operation?.Key, result.Key)) == true)
        {
            return true;
        }

        return IsVerifiedAuthorizationContinuationAuthorizationRequired(state, result);
    }

    private static bool IsVerifiedAuthorizationContinuationAuthorizationRequired(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal result) =>
        result.Key is not null &&
        result.Tool?.Receipt is
        {
            Status: AgentToolReceiptStatus.AuthorizationRequired,
            AuthorizationRequired: not null,
        } &&
        NyxIdChatActionContinuationCorrelation.TryMatch(
            state,
            state.ActiveTask,
            state.ActiveTurn,
            result.Key,
            out _);

    private static bool IsCredentialFreePostconditionTerminal(
        NyxIdChatOperationResultSignal result) =>
        result.ResultCase is
            NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition or
            NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification or
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure;

    private static byte[] ComputeResultDigest(NyxIdChatOperationResultSignal result) =>
        SHA256.HashData(result.ToByteArray());

    private static NyxIdChatOperationResultSignal BuildRejectedPostconditionResult(
        NyxIdChatOperationResultSignal result,
        string reasonCode,
        string safeMessage) =>
        new()
        {
            Key = result.Key?.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? PostconditionResultRejectedCode
                    : reasonCode,
                SafeMessage = string.IsNullOrWhiteSpace(safeMessage)
                    ? string.IsNullOrWhiteSpace(result.ActionPostcondition?.SafeMessage)
                        ? PostconditionResultRejectedMessage
                        : result.ActionPostcondition.SafeMessage
                    : safeMessage,
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        };

    private static NyxIdChatOperationFailure BuildFencedPostconditionFailure(
        NyxIdChatOperationResultSignal result) =>
        result.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Failure
            ? new NyxIdChatOperationFailure
            {
                FailureCode = result.Failure.FailureCode,
                SafeMessage = result.Failure.SafeMessage,
                ExternalEffect = result.Failure.ExternalEffect,
            }
            : new NyxIdChatOperationFailure
            {
                FailureCode = FencedPostconditionResultConsumedCode,
                SafeMessage = FencedPostconditionResultConsumedMessage,
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            };

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static bool IsValidOperationProgress(NyxIdChatOperationProgressSignal signal)
    {
        if (signal.Sequence <= 0 ||
            signal.ProgressCase == NyxIdChatOperationProgressSignal.ProgressOneofCase.None)
        {
            return false;
        }

        return signal.ProgressCase switch
        {
            NyxIdChatOperationProgressSignal.ProgressOneofCase.Phase =>
                signal.Phase is { } phase &&
                !string.IsNullOrWhiteSpace(phase.SubstepId) &&
                phase.SubstepId.Length <= 128 &&
                !string.IsNullOrWhiteSpace(phase.Title) &&
                phase.Title.Length <= 400 &&
                phase.Status is NyxIdChatSubstepStatus.Running or
                    NyxIdChatSubstepStatus.Done or
                    NyxIdChatSubstepStatus.Failed,
            NyxIdChatOperationProgressSignal.ProgressOneofCase.StreamingBatch =>
                signal.StreamingBatch.Segments.Count > 0 &&
                signal.StreamingBatch.Segments.All(static segment =>
                    segment.ProgressCase switch
                    {
                        NyxIdChatStreamingProgressSegment.ProgressOneofCase.Text =>
                            !string.IsNullOrEmpty(segment.Text.Delta),
                        NyxIdChatStreamingProgressSegment.ProgressOneofCase.Reasoning =>
                            !string.IsNullOrEmpty(segment.Reasoning.Delta),
                        _ => false,
                    }),
            _ => true,
        };
    }

    private static NyxIdChatStepChangeKind ResolveProgressStepChangeKind(
        NyxIdChatOperationState operation,
        NyxIdChatOperationProgressSignal signal,
        Timestamp committedAt)
    {
        if (signal.ProgressCase == NyxIdChatOperationProgressSignal.ProgressOneofCase.Phase)
            return NyxIdChatStepChangeKind.Substep;

        return operation.LastStepChangedAt is null ||
               committedAt.ToDateTimeOffset() >=
               operation.LastStepChangedAt.ToDateTimeOffset() + OperationStepChangedCadence
            ? NyxIdChatStepChangeKind.Status
            : NyxIdChatStepChangeKind.Unspecified;
    }

    private static bool IsValidPhaseTransition(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationProgressSignal signal)
    {
        if (signal.ProgressCase != NyxIdChatOperationProgressSignal.ProgressOneofCase.Phase)
            return true;

        var step = state.ActiveTask?.Steps.FirstOrDefault(candidate =>
            KeysEqual(candidate.Operation?.Key, signal.Key));
        var existing = step?.Substeps.FirstOrDefault(candidate =>
            string.Equals(candidate.SubstepId, signal.Phase.SubstepId, StringComparison.Ordinal));
        return existing is null
            ? signal.Phase.Status == NyxIdChatSubstepStatus.Running
            : existing.Status == NyxIdChatSubstepStatus.Running &&
              string.Equals(existing.Title, signal.Phase.Title, StringComparison.Ordinal);
    }

    private static void ApplyPhaseProgress(
        NyxIdChatTaskStepState step,
        NyxIdChatOperationPhaseProgress? phase)
    {
        if (phase is null)
            return;

        var substep = step.Substeps.FirstOrDefault(candidate =>
            string.Equals(candidate.SubstepId, phase.SubstepId, StringComparison.Ordinal));
        if (substep is null)
        {
            if (phase.Status != NyxIdChatSubstepStatus.Running)
                return;
            step.Substeps.Add(new NyxIdChatSubstepState
            {
                SubstepId = phase.SubstepId,
                Title = phase.Title,
                Status = phase.Status,
            });
            return;
        }

        if (substep.Status != NyxIdChatSubstepStatus.Running ||
            !string.Equals(substep.Title, phase.Title, StringComparison.Ordinal))
        {
            return;
        }

        substep.Status = phase.Status;
    }

    private static bool IsInFlight(NyxIdChatOperationPhase phase) =>
        phase is NyxIdChatOperationPhase.Requested or
            NyxIdChatOperationPhase.Dispatched or
            NyxIdChatOperationPhase.Running;

    private static bool TimestampsEqual(Timestamp? left, Timestamp? right) =>
        left is not null && right is not null && left.Equals(right);

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool OwnerMatches(string? persistedOwner, string? requestedOwner) =>
        string.Equals(
            NormalizeOptional(persistedOwner),
            NormalizeOptional(requestedOwner),
            StringComparison.Ordinal);
}
