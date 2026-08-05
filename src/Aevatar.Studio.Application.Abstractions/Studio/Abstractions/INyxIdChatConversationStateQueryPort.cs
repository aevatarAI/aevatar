using System.Text.Json.Serialization;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Reads the actor-scoped NyxIdChat conversation current-state replica.
/// Implementations must not activate actors, replay events, or prime a
/// projection from the query call stack.
/// </summary>
public interface INyxIdChatConversationStateQueryPort
{
    Task<NyxIdChatConversationStateQueryResult> GetAsync(
        NyxIdChatConversationStateQuery query,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>
        GetAttentionSummariesAsync(
            string scopeId,
            IReadOnlyCollection<string> actorIds,
            CancellationToken ct = default);
}

public sealed record NyxIdChatConversationAttentionSummary(
    string ActorId,
    string TaskStatus,
    string AttentionKind,
    DateTimeOffset? AttentionSince,
    string? ActiveStepSummary,
    long StateVersion);

public sealed record NyxIdChatConversationStateQuery(
    string ScopeId,
    string ActorId,
    long? AfterStateVersion = null,
    string? TurnId = null);

public enum NyxIdChatConversationStateQueryStatus
{
    Current = 0,
    NotModified = 1,
    ReloadRequired = 2,
    NotFound = 3,
}

public sealed record NyxIdChatConversationStateQueryResult(
    NyxIdChatConversationStateQueryStatus Status,
    long StateVersion,
    string? TurnId,
    string? ReasonCode,
    NyxIdChatConversationStateSnapshot? Snapshot)
{
    public static NyxIdChatConversationStateQueryResult Current(
        NyxIdChatConversationStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            NyxIdChatConversationStateQueryStatus.Current,
            snapshot.StateVersion,
            ResolveTurnId(snapshot),
            null,
            snapshot);
    }

    public static NyxIdChatConversationStateQueryResult NotModified(
        long stateVersion,
        string? turnId) =>
        new(
            NyxIdChatConversationStateQueryStatus.NotModified,
            stateVersion,
            turnId,
            null,
            null);

    public static NyxIdChatConversationStateQueryResult ReloadRequired(
        long stateVersion,
        string? turnId,
        string reasonCode) =>
        new(
            NyxIdChatConversationStateQueryStatus.ReloadRequired,
            stateVersion,
            turnId,
            string.IsNullOrWhiteSpace(reasonCode)
                ? "reload_required"
                : reasonCode.Trim(),
            null);

    public static NyxIdChatConversationStateQueryResult NotFound() =>
        new(
            NyxIdChatConversationStateQueryStatus.NotFound,
            0,
            null,
            null,
            null);

    private static string? ResolveTurnId(NyxIdChatConversationStateSnapshot snapshot) =>
        snapshot.ActiveTurn?.TurnId ?? snapshot.LatestTurn?.TurnId;
}

public sealed record NyxIdChatConversationStateSnapshot(
    string ActorId,
    string ScopeId,
    long StateVersion,
    long ProgressSequence,
    DateTimeOffset UpdatedAt,
    NyxIdChatConversationTurnSnapshot? ActiveTurn,
    NyxIdChatConversationTurnSnapshot? LatestTurn,
    IReadOnlyList<NyxIdChatConversationTurnSnapshot> RecentTerminalTurns,
    NyxIdChatConversationTaskSnapshot? ActiveTask,
    NyxIdChatPendingApprovalSnapshot? PendingApproval,
    IReadOnlyList<NyxIdChatActionSnapshot> PendingActions,
    NyxIdChatControlFenceSnapshot? ControlFence,
    NyxIdChatControlFenceSnapshot? LatestControlResult,
    NyxIdChatContinuationAdmissionSnapshot? ContinuationAdmission,
    NyxIdChatPendingInputSnapshot? PendingInput = null,
    NyxIdChatInputResolutionSnapshot? LatestInputResolution = null,
    NyxIdChatApprovalResolutionSnapshot? LatestApprovalResolution = null,
    string? TaskStatus = null,
    string? AttentionKind = null,
    DateTimeOffset? AttentionSince = null,
    string? ActiveStepSummary = null);

public sealed record NyxIdChatConversationTurnSnapshot(
    string TurnId,
    string TaskId,
    string Status,
    string? FailureCode,
    string? SafeMessage,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? TerminalAt,
    string? CommandId = null);

public sealed record NyxIdChatConversationTaskSnapshot(
    string TaskId,
    string TurnId,
    string Status,
    string? ActiveStepId,
    string? ActiveOperationId,
    string? FailureCode,
    string? SafeMessage,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<NyxIdChatConversationStepSnapshot> Steps);

public sealed record NyxIdChatConversationStepSnapshot(
    string StepId,
    int Order,
    string Kind,
    string Status,
    bool Required,
    string? Description,
    bool MayChangeExternalState,
    string ExternalEffect,
    string? ApprovalRequestId,
    string? ActionRequestId,
    string? FailureCode,
    string? SafeMessage,
    bool SafeToSkip,
    NyxIdChatAvailableActionsSnapshot AvailableActions,
    DateTimeOffset? UpdatedAt,
    NyxIdChatConversationOperationSnapshot? Operation);

public sealed record NyxIdChatAvailableActionsSnapshot(
    bool Retry,
    bool Skip,
    bool Stop);

public sealed record NyxIdChatConversationOperationSnapshot(
    string ConversationActorId,
    string TurnId,
    string TaskId,
    string StepId,
    string OperationId,
    long OperationGeneration,
    string Kind,
    string Phase,
    bool MayChangeExternalState,
    bool Idempotent,
    long LatestProgressSequence,
    string? TerminalCode,
    string? SafeMessage,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt);

public sealed record NyxIdChatPendingApprovalSnapshot(
    string ApprovalRequestId,
    string TurnId,
    string TaskId,
    string StepId,
    string ToolName,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AskedAt = null,
    string? Action = null,
    string? Target = null,
    string? ActorLabel = null,
    string? Reversibility = null,
    string? GrantBoundary = null,
    [property: JsonPropertyName("nyxidRequestId")] string? NyxIdRequestId = null);

public sealed record NyxIdChatInputOptionSnapshot(
    string OptionId,
    string Label,
    string? Description);

public sealed record NyxIdChatPendingInputSnapshot(
    string RequestId,
    string TurnId,
    string TaskId,
    string StepId,
    string Prompt,
    IReadOnlyList<NyxIdChatInputOptionSnapshot> Options,
    DateTimeOffset? AskedAt,
    bool AllowFreeText,
    bool MultiSelect);

public sealed record NyxIdChatInputResolutionSnapshot(
    string RequestId,
    string ClientRequestId,
    string Outcome,
    DateTimeOffset? CommittedAt);

public sealed record NyxIdChatApprovalResolutionSnapshot(
    string RequestId,
    string ClientRequestId,
    string Outcome,
    bool Approved,
    DateTimeOffset? CommittedAt);

public sealed record NyxIdChatControlFenceSnapshot(
    string Kind,
    string RequestId,
    string ClientRequestId,
    string TurnId,
    string TaskId,
    long OperationGeneration,
    string Outcome,
    string? ReasonCode,
    string? SafeMessage,
    DateTimeOffset? CommittedAt);

public sealed record NyxIdChatContinuationAdmissionSnapshot(
    string Kind,
    string RequestId,
    string ClientRequestId,
    string OriginTurnId,
    string ContinuationTurnId,
    string Status,
    string? ReasonCode,
    string? SafeMessage,
    DateTimeOffset? CommittedAt);

public sealed record NyxIdChatActionSnapshot(
    int SchemaVersion,
    string ActionRequestId,
    string OriginTurnId,
    string TaskId,
    string StepId,
    string Action,
    DateTimeOffset? RequestedAt,
    IReadOnlyList<NyxIdChatActionReportSnapshot> Reports,
    NyxIdChatActionPostconditionSnapshot? PostconditionResult);

public sealed record NyxIdChatActionReportSnapshot(
    string ActionRequestId,
    string OriginTurnId,
    string Disposition,
    NyxIdChatResourceSnapshot? Resource,
    string? SafeMessage,
    DateTimeOffset? ReportedAt);

public sealed record NyxIdChatActionPostconditionSnapshot(
    string ActionRequestId,
    string Disposition,
    bool Verified,
    NyxIdChatResourceSnapshot? Resource,
    string? FailureCode,
    string? SafeMessage);

public sealed record NyxIdChatResourceSnapshot(
    string? UserServiceId,
    string? KeyId,
    string? NodeId,
    string? ServiceAccountId,
    string? ClientId,
    string? DeviceId);
