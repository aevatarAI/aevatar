using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

[GAgent(NyxIdChatServiceDefaults.GAgentKind)]
public sealed class NyxIdChatConversationGAgent
    : GAgentBase<NyxIdChatConversationGAgentState>
{
    private const string SharedInputHistoryText = "Shared input content.";
    private static readonly TimeSpan HistoryInitializationRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryReservationRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryTerminalRetryDelay = TimeSpan.FromSeconds(5);

    public static string ProjectionKind => "nyxid-chat-conversation";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly AgentProfileTurnCatalogMaterializer? _turnCatalogMaterializer;

    public NyxIdChatConversationGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider)
        : this(actorRuntime, actorDispatchPort, timeProvider, null)
    {
    }

    public NyxIdChatConversationGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider,
        AgentProfileTurnCatalogMaterializer? turnCatalogMaterializer)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _turnCatalogMaterializer = turnCatalogMaterializer;
    }

    protected override NyxIdChatConversationGAgentState TransitionState(
        NyxIdChatConversationGAgentState current,
        IMessage evt)
    {
        var next = StateTransitionMatcher
            .Match(current, evt)
            .On<AgentProfileBoundEvent>(ApplyAgentProfileBound)
            .On<NyxIdChatConversationCreationStartedEvent>(ApplyConversationCreationStarted)
            .On<NyxIdChatConversationRegistrationAcceptedEvent>(ApplyConversationRegistrationAccepted)
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
            .On<NyxIdChatOperationProgressedEvent>(ApplyOperationProgressed)
            .On<NyxIdChatOperationReconciledEvent>(ApplyOperationReconciled)
            .On<NyxIdChatLateOperationEvidenceCommittedEvent>(ApplyLateOperationEvidenceCommitted)
            .On<NyxIdChatControlFenceCommittedEvent>(ApplyControlFenceCommitted)
            .On<NyxIdChatContinuationAdmissionCommittedEvent>(ApplyContinuationAdmissionCommitted)
            .On<NyxIdChatStepControlCommittedEvent>(ApplyStepControlCommitted)
            .On<NyxIdChatActionRequestedEvent>(ApplyActionRequested)
            .On<NyxIdChatInputRequestedEvent>(ApplyInputRequested)
            .On<NyxIdChatInputResolutionCommittedEvent>(ApplyInputResolutionCommitted)
            .On<NyxIdChatApprovalResolutionCommittedEvent>(ApplyApprovalResolutionCommitted)
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
            await DispatchHistoryInitializationContinuationAsync(
                    pendingInitialization,
                    ct);
        }

        if (State.HistoryDeliveryReservation is
            { Dispatched: false } pendingReservation)
        {
            try
            {
                await ReserveHistoryDeliveryAsync(pendingReservation, ct);
                await PersistDomainEventAsync(new NyxIdChatHistoryDeliveryReservationDispatchedEvent
                {
                    DeliveryId = pendingReservation.DeliveryId,
                    SourceCommandId = pendingReservation.SourceCommandId,
                    DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                }, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Logger.LogWarning(
                    "NyxIdChat pending history reservation recovery failed: actor={ActorId} delivery={DeliveryId} exceptionType={ExceptionType}",
                    Id,
                    pendingReservation.DeliveryId,
                    exception.GetType().Name);
                await ScheduleHistoryReservationRetryAsync(pendingReservation);
            }
        }

        if (State.PendingHistoryTerminal is { } pendingTerminal)
        {
            await DispatchPendingHistoryTerminalAsync();
        }

        var operation = ResolveOutstandingRecoveryOperation(State);
        if (operation?.Key is null)
            return;

        var version = CurrentCommittedVersion();
        var kind = operation.Kind == NyxIdChatStepKind.Postcondition &&
                   operation.Phase == NyxIdChatOperationPhase.Requested
            ? NyxIdChatRecoveryKind.PostconditionRedispatch
            : NyxIdChatRecoveryKind.InterruptedOperationReconciliation;
        var signal = new NyxIdChatRecoveryRequestedSignal
        {
            Key = operation.Key.Clone(),
            ExpectedStateVersion = version,
            Kind = kind,
        };
        var envelope = new EventEnvelope
        {
            Id = $"{operation.Key.OperationId}:recovery:{version}",
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(signal),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                Id,
                TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = operation.Key.OperationId,
            },
        };
        await _actorDispatchPort.DispatchAsync(Id, envelope, ct);
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
            string.Equals(State.ScopeId, scopeId, StringComparison.Ordinal) &&
            string.Equals(State.ConversationActorId, Id, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(State.HistoryInitializationOperationId))
        {
            await HandleStartTurnAsync(command.FirstTurn);
            return;
        }
        var commandId = ActiveInboundEnvelope?.Id ?? string.Empty;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ?? commandId;

        await BindAgentProfileAsync(command.AgentProfile);
        await PersistDomainEventAsync(new NyxIdChatConversationCreationStartedEvent
        {
            ScopeId = scopeId,
            ActorId = Id,
            CreatedLocally = command.CreatedLocally,
            CommandId = commandId,
            CorrelationId = correlationId,
            OwnerSubject = ownerSubject ?? string.Empty,
        }, CancellationToken.None);

        try
        {
            var receipt = await Services.GetRequiredService<IGAgentActorRegistryCommandPort>()
                .RegisterActorAsync(
                    new GAgentActorRegistration(scopeId, NyxIdChatServiceDefaults.GAgentKind, Id),
                    CancellationToken.None);
            if (receipt.IsAdmissionVisible)
            {
                var next = PrepareHistoryInitializationState(scopeId);
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

        if (command.FirstTurn is not null)
            await HandleStartTurnAsync(command.FirstTurn);

        if (State.PendingHistoryInitialization is not { } pendingInitialization)
            return;

        try
        {
            await DispatchHistoryInitializationContinuationAsync(
                    pendingInitialization,
                    CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                "NyxIdChat history initialization continuation dispatch failed after registration acceptance: actor={ActorId} operation={OperationId} exceptionType={ExceptionType}",
                Id,
                pendingInitialization.OperationId,
                exception.GetType().Name);
        }
    }

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
        }
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
                        pending.ObservedAt.ToDateTimeOffset()),
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
    public async Task HandleStartTurnAsync(NyxIdChatStartTurnCommand command)
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

        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var turnAuthority = await PrepareAgentProfileTurnAuthorityAsync(command);
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
            BuildStartedState(command, operationKey, turnAuthority, now));
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

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();
    }

    [EventHandler]
    public async Task HandleSteeringAsync(NyxIdChatSteeringCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var decision = NyxIdChatControlCommands.Steer(
            State,
            command,
            CurrentCommittedVersion(),
            now);
        if (!decision.ShouldCommit)
        {
            if (decision.StartContinuationNow && decision.Admission is not null)
                await DispatchSteeringContinuationAsync(command, decision.Admission);
            return;
        }

        var fencedState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.FencedState);
        var terminalPrepared = PrepareHistoryTerminalOutbox(fencedState);
        await PersistDomainEventAsync(new NyxIdChatControlFenceCommittedEvent
        {
            Fence = decision.Result.Clone(),
            Task = fencedState.ActiveTask?.Clone(),
            Turn = fencedState.ActiveTurn?.Clone(),
            State = fencedState,
        }, CancellationToken.None);

        if (decision.Admission is null)
        {
            if (terminalPrepared)
                await DispatchPendingHistoryTerminalAsync();
            return;
        }

        var continuationState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        continuationState.PendingHistoryTerminal = State.PendingHistoryTerminal?.Clone();

        await PersistDomainEventAsync(new NyxIdChatContinuationAdmissionCommittedEvent
        {
            Admission = decision.Admission.Clone(),
            State = continuationState,
        }, CancellationToken.None);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();

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

    [EventHandler]
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

        await PersistDomainEventAsync(new NyxIdChatApprovalResolutionCommittedEvent
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
    }

    [EventHandler]
    public async Task HandleOperationProgressAsync(NyxIdChatOperationProgressSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Sequence <= 0 ||
            signal.ProgressCase == NyxIdChatOperationProgressSignal.ProgressOneofCase.None ||
            !TryResolveCurrentOperation(signal.Key, out var operation) ||
            signal.Sequence <= operation.LatestProgressSequence)
        {
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatOperationProgressedEvent
        {
            Progress = signal.Clone(),
            ProgressSequence = State.ProgressSequence + 1,
            CommittedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleOperationResultAsync(NyxIdChatOperationResultSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!TryResolveCurrentOperation(signal.Key, out _))
            return;

        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var lateEvidence = NyxIdChatControlCommands.ReconcileLateOperationEvidence(
            State,
            signal,
            now);
        if (lateEvidence.IsFencedOperation)
        {
            if (!lateEvidence.ShouldCommit)
                return;

            var lateState = NyxIdChatNeedsYouDecisions.RefreshAttention(lateEvidence.State);
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
            return;
        }

        if (signal.ResultCase ==
            NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition)
        {
            var actionDecision = NyxIdChatBrowserActions.ReconcilePostcondition(
                State,
                signal,
                now);
            if (!actionDecision.ShouldCommit)
                return;

            var actionState = NyxIdChatNeedsYouDecisions.RefreshAttention(actionDecision.State);
            var actionTerminalPrepared = PrepareHistoryTerminalOutbox(actionState);

            await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
            {
                Result = BuildDurableResultEvidence(signal),
                Task = actionState.ActiveTask.Clone(),
                Turn = actionState.ActiveTurn.Clone(),
                ProgressSequence = actionState.ProgressSequence,
                State = actionState,
            }, CancellationToken.None);

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

        if (signal.Tool?.Receipt is
            {
                Status: AgentToolReceiptStatus.AuthorizationRequired,
                AuthorizationRequired: not null,
            })
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

                await PersistDomainEventAsync(new NyxIdChatActionRequestedEvent
                {
                    Request = actionDecision.Request.Clone(),
                    Task = actionState.ActiveTask.Clone(),
                    OriginTurn = actionState.ActiveTurn.Clone(),
                    State = actionState,
                }, CancellationToken.None);

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
            }
        }

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(State, signal, now);
        if (decision.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return;

        var nextState = NyxIdChatNeedsYouDecisions.RefreshAttention(decision.State);
        nextState.ProgressSequence = State.ProgressSequence + 1;
        nextState.UpdatedAt = now.Clone();
        var terminalText = signal.ResultCase ==
                           NyxIdChatOperationResultSignal.ResultOneofCase.Llm
            ? signal.Llm.Content
            : null;
        var terminalPrepared = PrepareHistoryTerminalOutbox(nextState, terminalText);

        await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
        {
            Result = BuildDurableResultEvidence(signal),
            Task = nextState.ActiveTask.Clone(),
            Turn = nextState.ActiveTurn.Clone(),
            ProgressSequence = nextState.ProgressSequence,
            State = nextState,
        }, CancellationToken.None);

        if (terminalPrepared)
            await DispatchPendingHistoryTerminalAsync();

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
            ActiveInboundEnvelope?.Propagation?.CorrelationId ?? signal.Key.OperationId,
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
                };
                durable.Llm.ToolCalls.AddRange(signal.Llm.ToolCalls.Select(static call =>
                    new NyxIdChatToolCall
                    {
                        CallId = call.CallId,
                        ToolName = call.ToolName,
                        Safety = call.Safety?.Clone(),
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
            case NyxIdChatOperationResultSignal.ResultOneofCase.Failure:
                durable.Failure = signal.Failure.Clone();
                break;
        }

        return durable;
    }

    private static AgentToolReceipt? BuildDurableReceiptEvidence(AgentToolReceipt? receipt)
    {
        if (receipt is null)
            return null;

        var durable = new AgentToolReceipt
        {
            CallId = receipt.CallId,
            ToolName = receipt.ToolName,
            Status = receipt.Status,
            ApprovalMode = receipt.ApprovalMode,
            IsDestructive = receipt.IsDestructive,
            SideEffectKind = receipt.SideEffectKind,
            SubjectKind = receipt.SubjectKind,
            SubjectId = receipt.SubjectId,
            SubjectVersion = receipt.SubjectVersion,
            SubjectHash = receipt.SubjectHash,
            ApprovalRequestId = receipt.ApprovalRequestId,
            ErrorCode = receipt.ErrorCode,
            ErrorMessage = receipt.ErrorMessage,
        };
        if (receipt.ManagedWorkflowHandoff is not null)
            durable.ManagedWorkflowHandoff = receipt.ManagedWorkflowHandoff.Clone();
        if (receipt.WorkflowRunDelivery is not null)
            durable.WorkflowRunDelivery = receipt.WorkflowRunDelivery.Clone();
        if (receipt.AuthorizationRequired is not null)
            durable.AuthorizationRequired = receipt.AuthorizationRequired.Clone();
        return durable;
    }

    private NyxIdChatConversationGAgentState BuildStartedState(
        NyxIdChatStartTurnCommand command,
        NyxIdChatOperationKey operationKey,
        AgentProfileTurnAuthorityState? turnAuthority,
        Timestamp now)
    {
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
        };
        turn.InputParts.AddRange(command.InputParts.Select(SanitizeInputPart));

        var step = new NyxIdChatTaskStepState
        {
            StepId = operationKey.StepId,
            Order = 1,
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
            CreatedAt = now.Clone(),
            UpdatedAt = now.Clone(),
        };
        task.Steps.Add(step);

        var next = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = Id,
            ScopeId = command.ScopeId.Trim(),
            OwnerSubject = State.OwnerSubject,
            RoleConfiguration = State.RoleConfiguration?.Clone(),
            AgentProfile = State.AgentProfile?.Clone(),
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            ProgressSequence = State.ProgressSequence + 1,
            UpdatedAt = now.Clone(),
        };
        next.PendingHistoryInitialization = State.PendingHistoryInitialization?.Clone();
        next.HistoryInitializationOperationId = State.HistoryInitializationOperationId;
        next.PendingHistoryTerminal = State.PendingHistoryTerminal?.Clone();
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
            Prompt = command.Prompt,
            SessionId = command.TurnId.Trim(),
            ScopeId = command.ScopeId.Trim(),
            CommandAttemptId = command.CommandId.Trim(),
            ToolContext = BuildActorOwnedToolContext(command.ToolContext).ToPayload(),
            LlmControl = command.LlmControl?.Clone(),
        };
        request.InputParts.AddRange(command.InputParts.Select(static part => part.Clone()));
        return request;
    }

    private async Task<AgentProfileTurnAuthorityState?> PrepareAgentProfileTurnAuthorityAsync(
        NyxIdChatStartTurnCommand command)
    {
        var profile = State.AgentProfile;
        if (profile is null || profile.ActivationMode == AgentProfileActivationMode.Shadow)
            return null;

        if (_turnCatalogMaterializer is null)
            return RestrictedEmptyAuthority(
                command.TurnId,
                AgentProfileTurnDegradationReason.MaterializerUnavailable);

        try
        {
            var toolContext = LLMControlContextMapper.FromPayload(command.LlmControl)
                .ToToolContext(BuildActorOwnedToolContext(command.ToolContext));
            return (await _turnCatalogMaterializer.PrepareAsync(
                    profile,
                    command.TurnId.Trim(),
                    command.Prompt ?? string.Empty,
                    registeredTools: [],
                    toolContext,
                    CancellationToken.None))
                .Authority;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(
                exception,
                "Agent profile turn authority preparation failed closed. turn={TurnId}",
                command.TurnId);
            return RestrictedEmptyAuthority(
                command.TurnId,
                AgentProfileTurnDegradationReason.MaterializationFailed);
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
        next.UpdatedAt = evt.DispatchedAt?.Clone();
        return next;
    }

    private static NyxIdChatConversationGAgentState ApplyOperationProgressed(
        NyxIdChatConversationGAgentState current,
        NyxIdChatOperationProgressedEvent evt)
    {
        var progress = evt.Progress;
        var next = current.Clone();
        var operation = next.ActiveTask?.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(candidate => KeysEqual(candidate?.Key, progress?.Key));
        if (operation is null ||
            progress is null ||
            progress.Sequence <= operation.LatestProgressSequence ||
            evt.ProgressSequence <= current.ProgressSequence)
        {
            return current;
        }

        operation.LatestProgressSequence = progress.Sequence;
        next.ProgressSequence = evt.ProgressSequence;
        next.UpdatedAt = evt.CommittedAt?.Clone();
        return next;
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

        var currentOperation = current.ActiveTask?.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(candidate => KeysEqual(candidate?.Key, evt.Result.Key));
        var reconciledOperation = evt.Task.Steps
            .Select(static step => step.Operation)
            .FirstOrDefault(candidate => KeysEqual(candidate?.Key, evt.Result.Key));
        if (currentOperation is null || reconciledOperation is null)
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
                !string.Equals(
                    evt.State.ActiveTurn.TurnId,
                    evt.Result.Key.TurnId,
                    StringComparison.Ordinal))
            {
                return current;
            }

            return evt.State.Clone();
        }

        var next = current.Clone();
        next.ActiveTask = evt.Task.Clone();
        next.ActiveTurn = evt.Turn.Clone();
        next.LatestTurn = evt.Turn.Clone();
        next.ProgressSequence = evt.ProgressSequence;
        next.UpdatedAt = evt.Turn.TerminalAt?.Clone() ?? evt.Task.UpdatedAt?.Clone();
        return next;
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

        return evt.State.Clone();
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

    private static NyxIdChatConversationGAgentState ApplyInputResolutionCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatInputResolutionCommittedEvent evt) =>
        evt.State?.Clone() ?? current;

    private static NyxIdChatConversationGAgentState ApplyApprovalResolutionCommitted(
        NyxIdChatConversationGAgentState current,
        NyxIdChatApprovalResolutionCommittedEvent evt) =>
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
            if (!existing.ToByteString().Equals(outbox.ToByteString()))
            {
                throw new InvalidOperationException(
                    "A different NyxIdChat history terminal delivery is already pending.");
            }

            return false;
        }

        state.PendingHistoryTerminal = outbox;
        return true;
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
        var envelope = new EventEnvelope
        {
            Id = BuildStableIdentity(
                "history-terminal-dispatch",
                pending.DeliveryId,
                pending.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(signal),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(Id, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = pending.SourceCommandId,
            },
        };
        return _actorDispatchPort.DispatchAsync(Id, envelope, ct);
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

    private Task DispatchHistoryInitializationContinuationAsync(
        NyxIdChatHistoryInitializationOutbox pending,
        CancellationToken ct)
    {
        var signal = new NyxIdChatHistoryInitializationDispatchRequested
        {
            OperationId = pending.OperationId,
            Attempt = pending.Attempt,
        };
        var envelope = new EventEnvelope
        {
            Id = BuildStableIdentity(
                "history-initialization-dispatch",
                pending.OperationId,
                pending.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(signal),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(Id, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = pending.OperationId,
            },
        };
        return _actorDispatchPort.DispatchAsync(Id, envelope, ct);
    }

    private Task DispatchInputRequestContinuationAsync(
        NyxIdChatInputRequestCommand command,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = $"{command.RequestId}:materialize",
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = Id },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = command.RequestId,
            },
        };
        return _actorDispatchPort.DispatchAsync(Id, envelope, ct);
    }

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

    private Task DispatchSteeringContinuationAsync(
        NyxIdChatSteeringCommand command,
        NyxIdChatContinuationAdmissionState admission)
    {
        var taskId = BuildStableIdentity(
            "task",
            Id,
            admission.ContinuationTurnId,
            admission.RequestId);
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
            ClientRequestId = command.ClientRequestId.Trim(),
            CommandId = continuationCommandId,
            CorrelationId = command.CorrelationId.Trim(),
            Prompt = command.Instruction.Trim(),
            ToolContext = command.ToolContext?.Clone(),
            LlmControl = command.LlmControl?.Clone(),
        };
        start.InputParts.AddRange(command.InputParts.Select(static part => part.Clone()));
        var envelope = new EventEnvelope
        {
            Id = continuationCommandId,
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(start),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = Id },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = command.CorrelationId.Trim(),
            },
        };
        return _actorDispatchPort.DispatchAsync(Id, envelope, CancellationToken.None);
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
            await PersistOperationDispatchFailureAsync(
                command.Key,
                "NYXID_CHAT_OPERATION_DISPATCH_FAILED",
                "The chat operation could not be dispatched.",
                exception);
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatOperationDispatchedEvent
        {
            Key = command.Key.Clone(),
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
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
            await PersistOperationDispatchFailureAsync(
                    operationKey,
                    "NYXID_CHAT_OPERATION_DISPATCH_FAILED",
                    "The chat operation could not be dispatched.",
                    exception);
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatOperationDispatchedEvent
        {
            Key = operationKey.Clone(),
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
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
            BuildInputPartsFingerprint(command.InputParts));

    private static string BuildInputPartsFingerprint(
        IEnumerable<Aevatar.AI.Abstractions.ChatContentPart> inputParts) =>
        BuildStableIdentity(
            "input-parts",
            inputParts
                .Select(static part => Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(part.ToByteArray())))
                .ToArray());

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

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
