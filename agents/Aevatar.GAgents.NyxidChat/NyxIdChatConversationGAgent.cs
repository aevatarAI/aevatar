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
            .OrCurrent();

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

            throw new InvalidOperationException("A NyxIdChat conversation already has an active turn.");
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

        var decision = NyxIdChatTaskTransitionPolicy.ReconcileOperation(State, signal);
        if (decision.Outcome != NyxIdChatTransitionOutcome.Accepted)
            return;

        var now = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var task = decision.State.ActiveTask.Clone();
        var turn = decision.State.ActiveTurn.Clone();
        var step = task.Steps.First(candidate => KeysEqual(candidate.Operation?.Key, signal.Key));
        step.Operation.CompletedAt = now.Clone();
        step.UpdatedAt = now.Clone();
        task.UpdatedAt = now.Clone();
        if (turn.Status != NyxIdChatTurnStatus.Active)
            turn.TerminalAt = now.Clone();

        await PersistDomainEventAsync(new NyxIdChatOperationReconciledEvent
        {
            Result = signal.Clone(),
            Task = task,
            Turn = turn,
            ProgressSequence = State.ProgressSequence + 1,
        }, CancellationToken.None).ConfigureAwait(false);
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

        return new NyxIdChatConversationGAgentState
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

        var next = current.Clone();
        next.ActiveTask = evt.Task.Clone();
        next.ActiveTurn = evt.Turn.Clone();
        next.LatestTurn = evt.Turn.Clone();
        next.ProgressSequence = evt.ProgressSequence;
        next.UpdatedAt = evt.Turn.TerminalAt?.Clone() ?? evt.Task.UpdatedAt?.Clone();
        return next;
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
}
