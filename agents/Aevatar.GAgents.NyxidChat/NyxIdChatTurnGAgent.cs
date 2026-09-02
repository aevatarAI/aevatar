using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

[GAgent(NyxIdChatServiceDefaults.TurnGAgentKind)]
public sealed class NyxIdChatTurnGAgent : GAgentBase<NyxIdChatTurnGAgentState>
{
    private const int DeliveredOperationHistoryLimit = 32;
    private const string ExecutionFailedCode = "NYXID_CHAT_OPERATION_EXECUTION_FAILED";
    private const string ExecutionFailedMessage = "The operation could not be completed.";
    private const string ResultKeyMismatchCode = "NYXID_CHAT_OPERATION_RESULT_KEY_MISMATCH";
    private const string ResultKeyMismatchMessage = "The operation result identity did not match the admitted operation.";
    private const string InterruptedCode = "NYXID_CHAT_OPERATION_INTERRUPTED";
    private const string InterruptedMessage =
        "The operation was interrupted and was not replayed automatically.";
    private const string OutcomeUncertainCode = "NYXID_CHAT_OPERATION_OUTCOME_UNCERTAIN";
    private const string OutcomeUncertainMessage =
        "The external operation may have changed state before recovery.";
    private const string ResultDeliveryLostCode =
        "NYXID_CHAT_OPERATION_RESULT_DELIVERY_LOST";
    private const string ResultDeliveryLostMessage =
        "The operation completed, but its original result could not be recovered for delivery.";

    private readonly INyxIdChatTurnOperationExecutor _operationExecutor;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly NyxIdChatTransientExecutionSession _executionSession = new();

    public NyxIdChatTurnGAgent(
        INyxIdChatTurnOperationExecutor operationExecutor,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider)
    {
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override NyxIdChatTurnGAgentState TransitionState(
        NyxIdChatTurnGAgentState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<NyxIdChatTurnOperationAdmittedEvent>(ApplyAdmitted)
            .On<NyxIdChatTurnOperationCompletedEvent>(ApplyCompleted)
            .On<NyxIdChatTurnOperationDeliveredEvent>(ApplyDelivered)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.AdmittedOperation is null || State.ResultDelivered)
            return;

        var version = CurrentCommittedVersion();
        var signal = new NyxIdChatRecoveryRequestedSignal
        {
            Key = State.AdmittedOperation.Clone(),
            ExpectedStateVersion = version,
            Kind = NyxIdChatRecoveryKind.InterruptedOperationReconciliation,
        };
        var envelope = new EventEnvelope
        {
            Id = $"{State.AdmittedOperation.OperationId}:turn-recovery:{version}",
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(signal),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                Id,
                TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = State.AdmittedOperation.OperationId,
            },
        };
        await _actorDispatchPort.DispatchAsync(Id, envelope, ct);
    }

    [EventHandler]
    public async Task HandleOperationAsync(NyxIdChatOperationDispatchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsValid(command) || IsDuplicateOrUnavailable(command.Key))
            return;

        var kind = ResolveKind(command);
        var admittedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        await PersistDomainEventAsync(new NyxIdChatTurnOperationAdmittedEvent
        {
            Key = command.Key.Clone(),
            OperationKind = kind,
            MayChangeExternalState = command.Tool?.MayChangeExternalState == true ||
                                     command.ToolApprovalContinuation?.MayChangeExternalState == true,
            AdmittedAt = admittedAt,
        }, CancellationToken.None);

        NyxIdChatOperationResultSignal result;
        try
        {
            var execution = await _operationExecutor.ExecuteAsync(
                    command,
                    _executionSession,
                    (progress, token) => DispatchProgressAsync(command.Key, progress, token),
                    CancellationToken.None);
            result = NormalizeResult(command, execution.Result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat turn operation failed: turnActor={TurnActorId} operation={OperationId}",
                Id,
                command.Key.OperationId);
            result = new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Failure = new NyxIdChatOperationFailure
                {
                    FailureCode = ExecutionFailedCode,
                    SafeMessage = ExecutionFailedMessage,
                    ExternalEffect = command.Tool?.MayChangeExternalState == true ||
                                     command.ToolApprovalContinuation?.MayChangeExternalState == true
                        ? NyxIdChatEffectEvidence.MayHaveChanged
                        : NyxIdChatEffectEvidence.NotApplied,
                },
            };
        }

        var completion = ClassifyCompletion(result);
        await PersistDomainEventAsync(new NyxIdChatTurnOperationCompletedEvent
        {
            Key = command.Key.Clone(),
            Phase = completion.Phase,
            TerminalCode = completion.TerminalCode,
            SafeMessage = completion.SafeMessage,
            ExternalEffect = completion.ExternalEffect,
            CompletedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);

        await DispatchResultAsync(command.Key.ConversationActorId, result);

        await PersistDomainEventAsync(new NyxIdChatTurnOperationDeliveredEvent
        {
            Key = command.Key.Clone(),
            DeliveredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleRecoveryRequestedAsync(NyxIdChatRecoveryRequestedSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Kind != NyxIdChatRecoveryKind.InterruptedOperationReconciliation ||
            signal.ExpectedStateVersion != CurrentCommittedVersion() ||
            signal.Key is null ||
            State.AdmittedOperation is null ||
            State.ResultDelivered ||
            !KeysEqual(State.AdmittedOperation, signal.Key))
        {
            return;
        }

        var completedUndelivered = State.CompletedAt is not null && IsTerminal(State.Phase);
        var effect = completedUndelivered
            ? NormalizeEffect(
                State.ExternalEffect,
                State.MayChangeExternalState
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied)
            : State.MayChangeExternalState
                ? NyxIdChatEffectEvidence.MayHaveChanged
                : NyxIdChatEffectEvidence.NotApplied;
        var result = new NyxIdChatOperationResultSignal
        {
            Key = signal.Key.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = completedUndelivered
                    ? ResultDeliveryLostCode
                    : effect == NyxIdChatEffectEvidence.MayHaveChanged
                    ? OutcomeUncertainCode
                    : InterruptedCode,
                SafeMessage = completedUndelivered
                    ? ResultDeliveryLostMessage
                    : effect == NyxIdChatEffectEvidence.MayHaveChanged
                    ? OutcomeUncertainMessage
                    : InterruptedMessage,
                ExternalEffect = effect,
            },
        };
        var completion = ClassifyCompletion(result);
        await PersistDomainEventAsync(new NyxIdChatTurnOperationCompletedEvent
        {
            Key = signal.Key.Clone(),
            Phase = completion.Phase,
            TerminalCode = completion.TerminalCode,
            SafeMessage = completion.SafeMessage,
            ExternalEffect = completion.ExternalEffect,
            CompletedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
        await DispatchResultAsync(signal.Key.ConversationActorId, result);
        await PersistDomainEventAsync(new NyxIdChatTurnOperationDeliveredEvent
        {
            Key = signal.Key.Clone(),
            DeliveredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
    }

    private bool IsDuplicateOrUnavailable(NyxIdChatOperationKey key)
    {
        if (State.DeliveredOperations.Any(delivered => KeysEqual(delivered, key)))
            return true;

        if (State.AdmittedOperation is null)
            return false;

        if (KeysEqual(State.AdmittedOperation, key))
            return true;

        if (!State.ResultDelivered || !IsTerminal(State.Phase))
            return true;

        return !SameTurn(State.AdmittedOperation, key);
    }

    private async Task DispatchProgressAsync(
        NyxIdChatOperationKey admittedKey,
        NyxIdChatOperationProgressSignal progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!KeysEqual(admittedKey, progress.Key))
            return;

        var envelope = CreateDirectEnvelope(
            admittedKey.ConversationActorId,
            $"{admittedKey.OperationId}:progress:{progress.Sequence}",
            progress);
        await _actorDispatchPort
            .DispatchAsync(admittedKey.ConversationActorId, envelope, ct);
    }

    private async Task DispatchResultAsync(
        string conversationActorId,
        NyxIdChatOperationResultSignal result)
    {
        var envelope = CreateDirectEnvelope(
            conversationActorId,
            $"{result.Key.OperationId}:result",
            result);
        await _actorDispatchPort
            .DispatchAsync(conversationActorId, envelope, CancellationToken.None);
    }

    private EventEnvelope CreateDirectEnvelope(string actorId, string envelopeId, IMessage payload) =>
        new()
        {
            Id = envelopeId,
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actorId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = ActiveInboundEnvelope?.Propagation?.CorrelationId
                    ?? payload.Descriptor.FullName,
            },
        };

    private static NyxIdChatOperationResultSignal NormalizeResult(
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatOperationResultSignal? result)
    {
        if (result is not null && KeysEqual(command.Key, result.Key))
            return result.Clone();

        return new NyxIdChatOperationResultSignal
        {
            Key = command.Key.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = ResultKeyMismatchCode,
                SafeMessage = ResultKeyMismatchMessage,
                ExternalEffect = command.Tool?.MayChangeExternalState == true ||
                                 command.ToolApprovalContinuation?.MayChangeExternalState == true
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied,
            },
        };
    }

    private static OperationCompletion ClassifyCompletion(NyxIdChatOperationResultSignal result)
    {
        if (result.Failure is not null)
        {
            return new OperationCompletion(
                result.Failure.ExternalEffect == NyxIdChatEffectEvidence.MayHaveChanged
                    ? NyxIdChatOperationPhase.Uncertain
                    : NyxIdChatOperationPhase.Failed,
                result.Failure.FailureCode,
                result.Failure.SafeMessage,
                NormalizeEffect(result.Failure.ExternalEffect, NyxIdChatEffectEvidence.NotApplied));
        }

        if (result.Tool is not null)
        {
            var receipt = result.Tool.Receipt;
            var phase = receipt?.Status switch
            {
                Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success or
                Aevatar.AI.Abstractions.AgentToolReceiptStatus.ApprovalRequired or
                Aevatar.AI.Abstractions.AgentToolReceiptStatus.AuthorizationRequired =>
                    NyxIdChatOperationPhase.Succeeded,
                Aevatar.AI.Abstractions.AgentToolReceiptStatus.Denied =>
                    NyxIdChatOperationPhase.Cancelled,
                _ => NyxIdChatOperationPhase.Failed,
            };
            return new OperationCompletion(
                phase,
                receipt?.ErrorCode ?? string.Empty,
                receipt?.ErrorMessage ?? string.Empty,
                NormalizeEffect(result.Tool.ExternalEffect, NyxIdChatEffectEvidence.NotApplied));
        }

        if (result.ActionPostcondition is not null)
        {
            return new OperationCompletion(
                result.ActionPostcondition.Verified
                    ? NyxIdChatOperationPhase.Succeeded
                    : NyxIdChatOperationPhase.Failed,
                result.ActionPostcondition.FailureCode,
                result.ActionPostcondition.SafeMessage,
                result.ActionPostcondition.Verified
                    ? NyxIdChatEffectEvidence.Confirmed
                    : NyxIdChatEffectEvidence.NotApplied);
        }

        return result.Llm is not null
            ? new OperationCompletion(
                NyxIdChatOperationPhase.Succeeded,
                string.Empty,
                string.Empty,
                NyxIdChatEffectEvidence.NotApplied)
            : new OperationCompletion(
                NyxIdChatOperationPhase.Failed,
                ExecutionFailedCode,
                ExecutionFailedMessage,
                NyxIdChatEffectEvidence.NotApplied);
    }

    private static NyxIdChatTurnGAgentState ApplyAdmitted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationAdmittedEvent evt)
    {
        var next = current.Clone();
        next.AdmittedOperation = evt.Key?.Clone();
        next.OperationKind = evt.OperationKind;
        next.Phase = NyxIdChatOperationPhase.Requested;
        next.TerminalCode = string.Empty;
        next.SafeMessage = string.Empty;
        next.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        next.MayChangeExternalState = evt.MayChangeExternalState;
        next.ResultDelivered = false;
        next.AdmittedAt = evt.AdmittedAt?.Clone();
        next.CompletedAt = null;
        next.DeliveredAt = null;
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyCompleted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationCompletedEvent evt)
    {
        if (!KeysEqual(current.AdmittedOperation, evt.Key))
            return current;

        var next = current.Clone();
        next.Phase = evt.Phase;
        next.TerminalCode = evt.TerminalCode;
        next.SafeMessage = evt.SafeMessage;
        next.ExternalEffect = evt.ExternalEffect;
        next.CompletedAt = evt.CompletedAt?.Clone();
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyDelivered(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationDeliveredEvent evt)
    {
        if (!KeysEqual(current.AdmittedOperation, evt.Key))
            return current;

        var next = current.Clone();
        next.ResultDelivered = true;
        next.DeliveredAt = evt.DeliveredAt?.Clone();
        if (!next.DeliveredOperations.Any(delivered => KeysEqual(delivered, evt.Key)))
            next.DeliveredOperations.Add(evt.Key.Clone());
        while (next.DeliveredOperations.Count > DeliveredOperationHistoryLimit)
            next.DeliveredOperations.RemoveAt(0);
        return next;
    }

    private static bool IsValid(NyxIdChatOperationDispatchCommand command) =>
        command.Key is
        {
            ConversationActorId.Length: > 0,
            TurnId.Length: > 0,
            TaskId.Length: > 0,
            StepId.Length: > 0,
            OperationId.Length: > 0,
            OperationGeneration: > 0,
        } &&
        command.InputCase is not NyxIdChatOperationDispatchCommand.InputOneofCase.None;

    private static NyxIdChatStepKind ResolveKind(NyxIdChatOperationDispatchCommand command) =>
        command.InputCase switch
        {
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm => NyxIdChatStepKind.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation => NyxIdChatStepKind.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool => NyxIdChatStepKind.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation =>
                NyxIdChatStepKind.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.ActionPostcondition =>
                NyxIdChatStepKind.Postcondition,
            _ => NyxIdChatStepKind.Unspecified,
        };

    private static bool SameTurn(NyxIdChatOperationKey left, NyxIdChatOperationKey right) =>
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal);

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        SameTurn(left, right) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static bool IsTerminal(NyxIdChatOperationPhase phase) =>
        phase is NyxIdChatOperationPhase.Succeeded or
            NyxIdChatOperationPhase.Failed or
            NyxIdChatOperationPhase.Cancelled or
            NyxIdChatOperationPhase.Uncertain;

    private static NyxIdChatEffectEvidence NormalizeEffect(
        NyxIdChatEffectEvidence value,
        NyxIdChatEffectEvidence fallback) =>
        value == NyxIdChatEffectEvidence.Unspecified ? fallback : value;

    private long CurrentCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException(
            "Event sourcing must be configured before recovery."))
        .CurrentVersion;

    private sealed record OperationCompletion(
        NyxIdChatOperationPhase Phase,
        string TerminalCode,
        string SafeMessage,
        NyxIdChatEffectEvidence ExternalEffect);
}
