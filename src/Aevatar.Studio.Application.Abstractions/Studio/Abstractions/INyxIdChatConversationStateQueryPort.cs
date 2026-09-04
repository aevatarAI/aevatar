using System.Text.Json.Serialization;
using Aevatar.Foundation.Abstractions.Tools;

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
    long StateVersion,
    IReadOnlyList<NyxIdChatConversationContextAttachmentSnapshot>? ContextAttachments = null);

public sealed record NyxIdChatConversationContextAttachmentSnapshot(
    string ArtifactId,
    string RevisionMode,
    string PinnedRevisionId);

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
    string? ActiveStepSummary = null,
    IReadOnlyList<NyxIdChatActionSnapshot>? RecentActions = null,
    NyxIdChatStepControlResultSnapshot? LatestStepControlResult = null,
    IReadOnlyList<NyxIdChatStepControlResultSnapshot>? RecentStepControlResults = null,
    NyxIdChatCanaryEffectFaultSnapshot? CanaryEffectFault = null,
    IReadOnlyList<NyxIdChatConversationContextAttachmentSnapshot>? ContextAttachments = null,
    NyxIdChatPendingWorkflowSignalSnapshot? PendingWorkflowSignal = null);

public sealed record NyxIdChatPendingWorkflowSignalSnapshot(
    string ActorId,
    string RunId,
    string SignalName,
    string? StepId,
    string? Prompt,
    long TimeoutMs,
    DateTimeOffset? ObservedAt);

public sealed record NyxIdChatCanaryEffectFaultSnapshot(
    string ArmId,
    string Status,
    NyxIdChatCanaryOperationSnapshot SourceOperation,
    NyxIdChatCanaryOperationSnapshot? TargetOperation,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ArmedAt,
    DateTimeOffset? ForwardedAt,
    DateTimeOffset? ConsumedAt);

public sealed record NyxIdChatCanaryOperationSnapshot(
    string ConversationActorId,
    string TurnId,
    string TaskId,
    string StepId,
    string OperationId,
    long OperationGeneration);

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
    IReadOnlyList<NyxIdChatConversationStepSnapshot> Steps,
    int SchemaVersion = 5,
    string? ActorId = null,
    string? PlanId = null,
    int PlanRevision = 1,
    string? Title = null,
    IReadOnlyList<NyxIdChatConversationPlanRevisionSnapshot>? PlanRevisions = null,
    int PlanRevisionHistoryStart = 0);

public sealed record NyxIdChatConversationPlanRevisionSnapshot(
    int PlanRevision,
    string RevisionCause,
    DateTimeOffset? CommittedAt,
    IReadOnlyList<string> AddedStepIds,
    IReadOnlyList<string> CancelledStepIds);

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
    NyxIdChatAvailableActionsSnapshot? AvailableActions,
    DateTimeOffset? UpdatedAt,
    NyxIdChatConversationOperationSnapshot? Operation,
    NyxIdChatConversationStepSourceSnapshot? Source = null,
    string? AddedBy = null,
    IReadOnlyList<string>? DependsOn = null,
    NyxIdChatConversationStepEstimateSnapshot? Estimate = null,
    IReadOnlyList<NyxIdChatConversationSubstepSnapshot>? Substeps = null,
    int AddedInPlanRevision = 0,
    int CancelledInPlanRevision = 0,
    NyxIdChatPostReturnApprovalObservationSnapshot? ApprovalObservation = null,
    NyxIdChatStepGuardSnapshot? Guard = null);

public sealed record NyxIdChatStepGuardSnapshot(
    string ConditionStepId,
    string RequiredOutcome);

public sealed record NyxIdChatPostReturnApprovalObservationSnapshot(
    string ApprovalRequestId,
    string DecisionMode,
    string ReceiptStatus,
    DateTimeOffset? ObservedAt,
    string? TerminalOutcome = null,
    string? SubjectKind = null);

public sealed record NyxIdChatConversationStepEstimateSnapshot(
    string Kind,
    int Seconds);

public sealed record NyxIdChatConversationSubstepSnapshot(
    string SubstepId,
    string Title,
    string Status);

public sealed record NyxIdChatConversationStepSourceSnapshot(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatLLMStepSourceSnapshot? Llm = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatToolStepSourceSnapshot? Tool = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatBrowserActionStepSourceSnapshot? BrowserAction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatPostconditionStepSourceSnapshot? Postcondition = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatInputStepSourceSnapshot? Input = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatApprovalStepSourceSnapshot? Approval = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatWebStepSourceSnapshot? Web = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatConditionStepSourceSnapshot? Condition = null);

public sealed record NyxIdChatLLMStepSourceSnapshot(string Model);

public sealed record NyxIdChatToolStepSourceSnapshot(
    string ToolName,
    string? ServiceSlug,
    string? ServiceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReadinessCapabilityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProviderResourceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ToolPresentationDescriptor? Presentation = null);

public sealed record NyxIdChatBrowserActionStepSourceSnapshot(
    string Action,
    string? ActionRequestId);

public sealed record NyxIdChatPostconditionStepSourceSnapshot(
    string? ActionRequestId,
    string? Check,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProviderResourceId = null);

public sealed record NyxIdChatInputStepSourceSnapshot(string? RequestId);

public sealed record NyxIdChatApprovalStepSourceSnapshot(string? ApprovalRequestId);

public sealed record NyxIdChatWebStepSourceSnapshot;

public sealed record NyxIdChatConditionStepSourceSnapshot(
    NyxIdChatNumericConditionSnapshot Condition);

public sealed record NyxIdChatNumericConditionSnapshot(
    string ConditionId,
    string SourceInputRequestId,
    long SuggestedThreshold,
    long EffectiveThreshold,
    string ThresholdOrigin,
    long ObservedValue,
    string Comparison,
    string Outcome,
    DateTimeOffset? EvaluatedAt,
    string GuardedToolName);

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
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LastProgressAt = null,
    DateTimeOffset? StalledAt = null);

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
    bool MultiSelect,
    NyxIdChatNumericThresholdInputSnapshot? NumericThreshold = null);

public sealed record NyxIdChatNumericThresholdInputSnapshot(
    long SuggestedValue,
    long MinimumValue,
    long MaximumValue);

public sealed record NyxIdChatInputResolutionSnapshot(
    string RequestId,
    string ClientRequestId,
    string Outcome,
    DateTimeOffset? CommittedAt,
    NyxIdChatNumericThresholdResolutionSnapshot? NumericThreshold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatInputAnswerSnapshot? Answer = null);

public sealed record NyxIdChatInputAnswerSnapshot(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FreeText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatInputSelectionAnswerSnapshot? Selection = null);

public sealed record NyxIdChatInputSelectionAnswerSnapshot(
    IReadOnlyList<string> OptionIds);

public sealed record NyxIdChatNumericThresholdResolutionSnapshot(
    long SuggestedValue,
    long EffectiveValue,
    string Origin);

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

public sealed record NyxIdChatStepControlResultSnapshot(
    string Kind,
    string RequestId,
    string ClientRequestId,
    string TurnId,
    string TaskId,
    string StepId,
    long ExpectedOperationGeneration,
    long OperationGeneration,
    string Outcome,
    string? ReasonCode,
    string? SafeMessage,
    string CommandId,
    string CorrelationId,
    DateTimeOffset? CommittedAt,
    long ExpectedStateVersion,
    string ScopeId,
    string ConversationActorId);

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
    NyxIdChatActionPostconditionSnapshot? PostconditionResult,
    NyxIdChatActionRequestSnapshot? Request = null);

public sealed record NyxIdChatActionRequestSnapshot(
    int SchemaVersion,
    string ActorId,
    string OriginTurnId,
    string TaskId,
    string StepId,
    string ActionRequestId,
    string Action,
    NyxIdChatActionParamsSnapshot Params);

public sealed record NyxIdChatActionParamsSnapshot(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatCatalogServiceConnectSnapshot? CatalogService = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatCustomServiceConnectSnapshot? CustomService = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    NyxIdChatServiceAccessReviewSnapshot? ServiceAccessReview = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Platform = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? AllowedServiceIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? KeyId = null);

public sealed record NyxIdChatCatalogServiceConnectSnapshot(
    string ServiceSlug,
    IReadOnlyList<string> RequestedScopes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ViaNodeId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TargetOrgId);

public sealed record NyxIdChatCustomServiceConnectSnapshot(
    string Name,
    string EndpointUrl,
    string AuthMethod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AuthKeyName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ViaNodeId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TargetOrgId);

public sealed record NyxIdChatServiceAccessReviewSnapshot(
    string UserServiceId,
    string ServiceSlug,
    string ResourceUri);

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
