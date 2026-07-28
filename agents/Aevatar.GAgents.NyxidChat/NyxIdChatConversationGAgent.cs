using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
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
    public static string ProjectionKind => "nyxid-chat-conversation";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;

    public NyxIdChatConversationGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override NyxIdChatConversationGAgentState TransitionState(
        NyxIdChatConversationGAgentState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<AgentProfileBoundEvent>(ApplyAgentProfileBound)
            .On<NyxIdChatConversationCreationStartedEvent>(ApplyConversationCreationStarted)
            .On<NyxIdChatTurnStartedEvent>(ApplyTurnStarted)
            .On<NyxIdChatOperationDispatchedEvent>(ApplyOperationDispatched)
            .On<NyxIdChatOperationProgressedEvent>(ApplyOperationProgressed)
            .On<NyxIdChatOperationReconciledEvent>(ApplyOperationReconciled)
            .On<NyxIdChatLateOperationEvidenceCommittedEvent>(ApplyLateOperationEvidenceCommitted)
            .On<NyxIdChatControlFenceCommittedEvent>(ApplyControlFenceCommitted)
            .On<NyxIdChatContinuationAdmissionCommittedEvent>(ApplyContinuationAdmissionCommitted)
            .On<NyxIdChatStepControlCommittedEvent>(ApplyStepControlCommitted)
            .On<NyxIdChatActionRequestedEvent>(ApplyActionRequested)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct).ConfigureAwait(false);

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
        await _actorDispatchPort.DispatchAsync(Id, envelope, ct).ConfigureAwait(false);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleCreateConversationAsync(
        NyxIdChatConversationCreateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scopeId = NormalizeRequired(command.ScopeId, nameof(command.ScopeId));
        var commandId = ActiveInboundEnvelope?.Id ?? string.Empty;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId ?? commandId;

        await BindAgentProfileAsync(command.AgentProfile).ConfigureAwait(false);
        await PersistDomainEventAsync(new NyxIdChatConversationCreationStartedEvent
        {
            ScopeId = scopeId,
            ActorId = Id,
            CreatedLocally = command.CreatedLocally,
            CommandId = commandId,
            CorrelationId = correlationId,
        }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var receipt = await Services.GetRequiredService<IGAgentActorRegistryCommandPort>()
                .RegisterActorAsync(
                    new GAgentActorRegistration(scopeId, NyxIdChatServiceDefaults.GAgentKind, Id),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (receipt.IsAdmissionVisible)
            {
                await PersistDomainEventAsync(new NyxIdChatConversationRegistrationAcceptedEvent
                {
                    ScopeId = scopeId,
                    ActorId = Id,
                    CommandId = commandId,
                    CorrelationId = correlationId,
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await PersistRegistrationUnavailableAndCompensateAsync(
                    scopeId,
                    command.CreatedLocally,
                    "registration_not_admission_visible",
                    commandId,
                    correlationId)
                .ConfigureAwait(false);
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
                    correlationId)
                .ConfigureAwait(false);
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
                    CancellationToken.None)
                .ConfigureAwait(false);
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
            await _actorRuntime.DestroyAsync(command.ActorId, CancellationToken.None)
                .ConfigureAwait(false);
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
        }, CancellationToken.None).ConfigureAwait(false);
        await registry.UnregisterActorAsync(
                new GAgentActorRegistration(scopeId, NyxIdChatServiceDefaults.GAgentKind, Id),
                CancellationToken.None)
            .ConfigureAwait(false);
        await PersistDomainEventAsync(new NyxIdChatConversationUnregisteredEvent
        {
            ScopeId = scopeId,
            ActorId = Id,
            CommandId = commandId,
            CorrelationId = correlationId,
        }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            await Services.GetRequiredService<IChatHistoryCommandPort>()
                .DeleteConversationAsync(scopeId, Id, CancellationToken.None)
                .ConfigureAwait(false);
            await PersistDomainEventAsync(new NyxIdChatConversationHistoryDeletedEvent
            {
                ScopeId = scopeId,
                ActorId = Id,
                CommandId = commandId,
                CorrelationId = correlationId,
            }, CancellationToken.None).ConfigureAwait(false);
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
            }, CancellationToken.None).ConfigureAwait(false);
            await HandleDeletionCompensationAsync(new NyxIdChatConversationDeletionCompensationRequested
            {
                ScopeId = scopeId,
                ActorId = Id,
                Reason = "history_delete_failed",
            }).ConfigureAwait(false);
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
                    CancellationToken.None)
                .ConfigureAwait(false);
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

        if (State.ActiveTurn is not null)
        {
            if (SameTurnAdmission(State, command))
                return;

            if (State.ActiveTurn.Status == NyxIdChatTurnStatus.Active)
            {
                await PersistDomainEventAsync(new NyxIdChatTurnAdmissionRejectedEvent
                {
                    ConversationActorId = Id,
                    RequestedTurnId = command.TurnId.Trim(),
                    ActiveTurnId = State.ActiveTurn.TurnId,
                    CommandId = command.CommandId.Trim(),
                    CorrelationId = command.CorrelationId.Trim(),
                    ReasonCode = NyxIdChatControlCommands.ActiveTurnRequiresSteering,
                    SafeMessage = NyxIdChatControlCommands.ActiveTurnRequiresSteeringMessage,
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var operationKey = new NyxIdChatOperationKey
        {
            ConversationActorId = Id,
            TurnId = command.TurnId.Trim(),
            TaskId = command.TaskId.Trim(),
            StepId = BuildStableIdentity("step", Id, command.TurnId, command.TaskId, "llm"),
            OperationId = BuildStableIdentity("operation", Id, command.TurnId, command.TaskId, "llm", "1"),
            OperationGeneration = 1,
        };
        var next = BuildStartedState(command, operationKey, now);

        await PersistDomainEventAsync(new NyxIdChatTurnStartedEvent
        {
            State = next,
        }, CancellationToken.None).ConfigureAwait(false);

        var turnActorId = NyxIdChatTurnActorIds.ForTurn(Id, command.TurnId);
        var turnActor = await _actorRuntime
            .CreateAsync<NyxIdChatTurnGAgent>(turnActorId, CancellationToken.None)
            .ConfigureAwait(false);
        await _actorRuntime.LinkAsync(Id, turnActor.Id, CancellationToken.None).ConfigureAwait(false);

        var dispatchCommand = new NyxIdChatOperationDispatchCommand
        {
            Key = operationKey.Clone(),
            Llm = new NyxIdChatLLMOperationInput
            {
                Request = BuildTransientChatRequest(command),
            },
        };
        var envelope = new EventEnvelope
        {
            Id = operationKey.OperationId,
            Timestamp = now.Clone(),
            Payload = Any.Pack(dispatchCommand),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = turnActor.Id },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = command.CorrelationId.Trim(),
            },
        };
        await _actorDispatchPort
            .DispatchAsync(turnActor.Id, envelope, CancellationToken.None)
            .ConfigureAwait(false);

        await PersistDomainEventAsync(new NyxIdChatOperationDispatchedEvent
        {
            Key = operationKey.Clone(),
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None).ConfigureAwait(false);
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

        await PersistDomainEventAsync(new NyxIdChatControlFenceCommittedEvent
        {
            Fence = decision.Result.Clone(),
            Task = decision.State.ActiveTask?.Clone(),
            Turn = decision.State.ActiveTurn?.Clone(),
            State = decision.State.Clone(),
        }, CancellationToken.None).ConfigureAwait(false);
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
                await DispatchSteeringContinuationAsync(command, decision.Admission)
                    .ConfigureAwait(false);
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatControlFenceCommittedEvent
        {
            Fence = decision.Result.Clone(),
            Task = decision.FencedState.ActiveTask?.Clone(),
            Turn = decision.FencedState.ActiveTurn?.Clone(),
            State = decision.FencedState.Clone(),
        }, CancellationToken.None).ConfigureAwait(false);

        if (decision.Admission is null)
            return;

        await PersistDomainEventAsync(new NyxIdChatContinuationAdmissionCommittedEvent
        {
            Admission = decision.Admission.Clone(),
            State = decision.State.Clone(),
        }, CancellationToken.None).ConfigureAwait(false);

        if (decision.StartContinuationNow)
            await DispatchSteeringContinuationAsync(command, decision.Admission).ConfigureAwait(false);
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
            await PersistDomainEventAsync(new NyxIdChatStepControlCommittedEvent
            {
                Result = decision.Result.Clone(),
                State = decision.State.Clone(),
            }, CancellationToken.None).ConfigureAwait(false);
        }

        if (!decision.ShouldDispatch || decision.NextCommand is null)
            return;

        await DispatchAuthorizedOperationAsync(
                decision.NextCommand,
                command.CorrelationId,
                now)
            .ConfigureAwait(false);
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

        await PersistDomainEventAsync(new NyxIdChatStepControlCommittedEvent
        {
            Result = decision.Result.Clone(),
            State = decision.State.Clone(),
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleActionContinueAsync(NyxIdChatActionContinueCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var decision = NyxIdChatBrowserActions.Continue(State, command, now);
        if (decision.ShouldCommit)
        {
            await PersistDomainEventAsync(new NyxIdChatContinuationAdmissionCommittedEvent
            {
                Admission = decision.Admission.Clone(),
                State = decision.State.Clone(),
            }, CancellationToken.None).ConfigureAwait(false);
        }

        if (!decision.ShouldDispatch || decision.NextCommand is null)
            return;

        await EnsureTurnActorAsync(decision.NextCommand.Key.TurnId).ConfigureAwait(false);
        await DispatchAuthorizedOperationAsync(
                decision.NextCommand,
                command.CorrelationId,
                now)
            .ConfigureAwait(false);
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

            await EnsureTurnActorAsync(command.Key.TurnId).ConfigureAwait(false);
            await DispatchAuthorizedOperationAsync(
                    command,
                    ActiveInboundEnvelope?.Propagation?.CorrelationId ??
                    command.Key.OperationId,
                    Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()))
                .ConfigureAwait(false);
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

        var nextState = decision.State.Clone();
        nextState.ProgressSequence = State.ProgressSequence + 1;
        nextState.UpdatedAt = now.Clone();
        await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
        {
            Result = recoveryResult,
            Task = nextState.ActiveTask.Clone(),
            Turn = nextState.ActiveTurn.Clone(),
            ProgressSequence = nextState.ProgressSequence,
            State = nextState,
        }, CancellationToken.None).ConfigureAwait(false);
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
        }, CancellationToken.None).ConfigureAwait(false);
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

            await PersistDomainEventAsync(new NyxIdChatLateOperationEvidenceCommittedEvent
            {
                Key = signal.Key.Clone(),
                OperationPhase = lateEvidence.OperationPhase,
                ExternalEffect = lateEvidence.ExternalEffect,
                ToolReceipt = BuildDurableReceiptEvidence(signal.Tool?.Receipt),
                TerminalCode = lateEvidence.TerminalCode,
                SafeMessage = lateEvidence.SafeMessage,
                ProgressSequence = lateEvidence.State.ProgressSequence,
                CommittedAt = now.Clone(),
                State = lateEvidence.State.Clone(),
            }, CancellationToken.None).ConfigureAwait(false);
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

            await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
            {
                Result = BuildDurableResultEvidence(signal),
                Task = actionDecision.State.ActiveTask.Clone(),
                Turn = actionDecision.State.ActiveTurn.Clone(),
                ProgressSequence = actionDecision.State.ProgressSequence,
                State = actionDecision.State.Clone(),
            }, CancellationToken.None).ConfigureAwait(false);

            if (!actionDecision.ShouldDispatch || actionDecision.NextCommand is null)
                return;

            await DispatchAuthorizedOperationAsync(
                    actionDecision.NextCommand,
                    ActiveInboundEnvelope?.Propagation?.CorrelationId ??
                    signal.Key.OperationId,
                    now)
                .ConfigureAwait(false);
            return;
        }

        if (signal.Tool?.Receipt is
            {
                Status: AgentToolReceiptStatus.AuthorizationRequired,
                AuthorizationRequired: not null,
            })
        {
            var actionDecision = NyxIdChatBrowserActions.RequestAuthorization(
                State,
                signal,
                Services.GetRequiredService<NyxIdAssistantActionRegistry>(),
                now);
            if (!actionDecision.ShouldCommit)
                return;

            await PersistDomainEventAsync(new NyxIdChatActionRequestedEvent
            {
                Request = actionDecision.Request.Clone(),
                Task = actionDecision.State.ActiveTask.Clone(),
                OriginTurn = actionDecision.State.ActiveTurn.Clone(),
                State = actionDecision.State.Clone(),
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var decision = NyxIdChatTaskLifecycle.ApplyOperationResult(State, signal, now);
        if (decision.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return;

        var nextState = decision.State.Clone();
        nextState.ProgressSequence = State.ProgressSequence + 1;
        nextState.UpdatedAt = now.Clone();

        await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
        {
            Result = BuildDurableResultEvidence(signal),
            Task = nextState.ActiveTask.Clone(),
            Turn = nextState.ActiveTurn.Clone(),
            ProgressSequence = nextState.ProgressSequence,
            State = nextState,
        }, CancellationToken.None).ConfigureAwait(false);

        if (decision.NextCommand is null)
            return;

        var turnActorId = NyxIdChatTurnActorIds.ForTurn(Id, signal.Key.TurnId);
        var envelope = new EventEnvelope
        {
            Id = decision.NextCommand.Key.OperationId,
            Timestamp = now.Clone(),
            Payload = Any.Pack(decision.NextCommand),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = turnActorId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = ActiveInboundEnvelope?.Propagation?.CorrelationId
                    ?? signal.Key.OperationId,
            },
        };
        await _actorDispatchPort
            .DispatchAsync(turnActorId, envelope, CancellationToken.None)
            .ConfigureAwait(false);

        await PersistDomainEventAsync(new NyxIdChatOperationDispatchedEvent
        {
            Key = decision.NextCommand.Key.Clone(),
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None).ConfigureAwait(false);
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
        Timestamp now)
    {
        var turn = new NyxIdChatTurnState
        {
            TurnId = command.TurnId.Trim(),
            TaskId = command.TaskId.Trim(),
            ClientRequestId = command.ClientRequestId.Trim(),
            Status = NyxIdChatTurnStatus.Active,
            Prompt = command.Prompt,
            CreatedAt = now.Clone(),
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
            RoleConfiguration = State.RoleConfiguration?.Clone(),
            AgentProfile = State.AgentProfile?.Clone(),
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            ProgressSequence = State.ProgressSequence + 1,
            UpdatedAt = now.Clone(),
        };
        next.RecentTerminalTurns.AddRange(
            State.RecentTerminalTurns.Select(static summary => summary.Clone()));
        next.RecentStepControlResults.AddRange(
            State.RecentStepControlResults.Select(static result => result.Clone()));
        next.LatestStepControlResult = State.LatestStepControlResult?.Clone();
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

    private static Aevatar.AI.Abstractions.ChatRequestEvent BuildTransientChatRequest(
        NyxIdChatStartTurnCommand command)
    {
        var request = new Aevatar.AI.Abstractions.ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = command.TurnId.Trim(),
            ScopeId = command.ScopeId.Trim(),
            CommandAttemptId = command.CommandId.Trim(),
            ToolContext = command.ToolContext?.Clone(),
            LlmControl = command.LlmControl?.Clone(),
        };
        request.InputParts.AddRange(command.InputParts.Select(static part => part.Clone()));
        return request;
    }

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
        next.ConversationActorId = evt.ActorId;
        next.ScopeId = evt.ScopeId;
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
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        if (!AgentProfileSnapshotCodec.ByteEquivalent(State.AgentProfile, profile))
            throw new InvalidOperationException("A conversation cannot replace its bound agent profile.");
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
        }, CancellationToken.None).ConfigureAwait(false);
        await HandleCreationCompensationAsync(new NyxIdChatConversationCreationCompensationRequested
        {
            ScopeId = scopeId,
            ActorId = Id,
            DestroyActor = destroyActor,
            Reason = reason,
        }).ConfigureAwait(false);
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
        await _actorDispatchPort
            .DispatchAsync(turnActorId, envelope, CancellationToken.None)
            .ConfigureAwait(false);
        await PersistDomainEventAsync(new NyxIdChatOperationDispatchedEvent
        {
            Key = command.Key.Clone(),
            DispatchedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EnsureTurnActorAsync(string turnId)
    {
        var turnActorId = NyxIdChatTurnActorIds.ForTurn(Id, turnId);
        var turnActor = await _actorRuntime
            .CreateAsync<NyxIdChatTurnGAgent>(turnActorId, CancellationToken.None)
            .ConfigureAwait(false);
        await _actorRuntime.LinkAsync(Id, turnActor.Id, CancellationToken.None)
            .ConfigureAwait(false);
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

    private static bool SameTurnAdmission(
        NyxIdChatConversationGAgentState state,
        NyxIdChatStartTurnCommand command) =>
        string.Equals(state.ConversationActorId, command.ConversationActorId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ScopeId, command.ScopeId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.TurnId, command.TurnId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.TaskId, command.TaskId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.ClientRequestId, command.ClientRequestId.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn?.Prompt, command.Prompt, StringComparison.Ordinal);

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
}
