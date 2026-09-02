using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class GAgentRunTerminalProjector
    : ICurrentStateProjectionMaterializer<GAgentRunTerminalProjectionContext>
{
    private const string LegacyLlmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const string LegacyLlmFailedPrefix = "LLM request failed";
    private const string ReasonCodeLegacyLlmError = "legacy_llm_error";

    private readonly IProjectionWriteDispatcher<GAgentRunTerminalReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public GAgentRunTerminalProjector(
        IProjectionWriteDispatcher<GAgentRunTerminalReadModel> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        GAgentRunTerminalProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(
                envelope,
                out var payload,
                out var eventId,
                out var stateVersion) ||
            payload == null)
        {
            return;
        }

        string sessionId;
        GAgentRunTerminalStatus status;
        string reasonCode;
        string reasonMessage;
        if (payload.Is(RoleChatSessionCompletedEvent.Descriptor))
        {
            var completed = payload.Unpack<RoleChatSessionCompletedEvent>();
            sessionId = completed.SessionId;
            (status, reasonCode, reasonMessage) = ResolveTerminal(completed);
        }
        else if (payload.Is(RoleChatCommandAttemptRejectedEvent.Descriptor))
        {
            var rejection = payload.Unpack<RoleChatCommandAttemptRejectedEvent>();
            if (rejection.Reason != RoleChatCommandAttemptRejectionReason.CapacityExhausted)
                return;

            sessionId = rejection.RequestedSessionId;
            status = GAgentRunTerminalStatus.Failed;
            reasonCode = GAgentRunFailureCodes.CapacityExhausted;
            var safeMessage = rejection.SafeMessage?.Trim() ?? string.Empty;
            reasonMessage = string.IsNullOrWhiteSpace(safeMessage) ? reasonCode : safeMessage;
        }
        else
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var correlationId = envelope.Propagation?.CorrelationId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(context.CorrelationId) ||
            !string.Equals(correlationId, context.CorrelationId, StringComparison.Ordinal))
        {
            return;
        }

        var documentId = BuildDocumentId(context.RootActorId, correlationId);
        var observedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var document = new GAgentRunTerminalReadModel
        {
            Id = documentId,
            ActorId = context.RootActorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            SessionId = sessionId,
            CorrelationId = correlationId,
            InteractionKind = (int)context.InteractionKind,
            Status = (int)status,
            ReasonCode = reasonCode,
            ReasonMessage = reasonMessage,
            ObservedAt = observedAt,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    public static string BuildDocumentId(string actorId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return $"gagent-run-terminal:{actorId}:{key}";
    }

    private static (GAgentRunTerminalStatus status, string reasonCode, string reasonMessage) ResolveTerminal(
        RoleChatSessionCompletedEvent completed)
    {
        if (completed.Outcome is RoleChatSessionOutcome.Failed or RoleChatSessionOutcome.OutcomeUncertain)
        {
            var failureCode = completed.FailureCode?.Trim() ?? string.Empty;
            if (completed.Outcome == RoleChatSessionOutcome.OutcomeUncertain &&
                string.IsNullOrWhiteSpace(failureCode))
            {
                failureCode = GAgentRunFailureCodes.OutcomeUncertain;
            }
            var safeMessage = completed.SafeMessage?.Trim() ?? string.Empty;
            return (
                completed.Outcome == RoleChatSessionOutcome.OutcomeUncertain
                    ? GAgentRunTerminalStatus.OutcomeUncertain
                    : GAgentRunTerminalStatus.Failed,
                failureCode,
                string.IsNullOrWhiteSpace(safeMessage) ? failureCode : safeMessage);
        }

        if (completed.Outcome != RoleChatSessionOutcome.Unspecified)
            return (GAgentRunTerminalStatus.TextMessageCompleted, string.Empty, string.Empty);

        var content = completed.Content ?? string.Empty;
        if (content.StartsWith(LegacyLlmErrorPrefix, StringComparison.Ordinal))
        {
            var legacyMessage = content[LegacyLlmErrorPrefix.Length..].Trim();
            var separatorIndex = legacyMessage.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                var candidateReasonCode = legacyMessage[..separatorIndex].Trim();
                if (IsKnownReasonCode(candidateReasonCode))
                {
                    return (
                        GAgentRunTerminalStatus.Failed,
                        candidateReasonCode,
                        legacyMessage[(separatorIndex + 1)..].Trim());
                }
            }

            return (
                GAgentRunTerminalStatus.Failed,
                ReasonCodeLegacyLlmError,
                legacyMessage);
        }

        if (content.StartsWith(LegacyLlmFailedPrefix, StringComparison.Ordinal))
        {
            return (
                GAgentRunTerminalStatus.Failed,
                ReasonCodeLegacyLlmError,
                content.Trim());
        }

        return (GAgentRunTerminalStatus.TextMessageCompleted, string.Empty, string.Empty);
    }

    private static bool IsKnownReasonCode(string reasonCode) =>
        reasonCode is
            "approval_continuation_failed" or
            "approval_denied" or
            "approval_timeout";
}
