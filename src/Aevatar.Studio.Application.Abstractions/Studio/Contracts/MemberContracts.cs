namespace Aevatar.Studio.Application.Studio.Contracts;

using System.Text.Json.Serialization;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Workflow.Abstractions;

/// <summary>
/// Wire-format implementation kind for HTTP/JSON. Uses lowercase strings so
/// Studio's HTTP surface stays member-centric and frontend-friendly. Mapped
/// onto <c>StudioMemberImplementationKind</c> at the boundary.
/// </summary>
public static class MemberImplementationKindNames
{
    public const string Workflow = "workflow";
    public const string Script = "script";
    public const string GAgent = "gagent";
}

/// <summary>
/// Wire-format lifecycle stage. Mirrors the
/// <c>StudioMemberLifecycleStage</c> proto enum but with stable string
/// values that Studio's frontend can switch on without taking a generated
/// proto dependency.
/// </summary>
public static class MemberLifecycleStageNames
{
    public const string Created = "created";
    public const string BuildReady = "build_ready";
    public const string BindReady = "bind_ready";
}

public static class StudioMemberBindingRunStatusNames
{
    public const string Accepted = "accepted";
    public const string AdmissionPending = "admission_pending";
    public const string Admitted = "admitted";
    public const string PlatformBindingPending = "platform_binding_pending";
    public const string MemberNotificationPending = "member_notification_pending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string Unknown = "unknown";
}

public static class StudioMemberBindingAckStageNames
{
    public const string DispatchAccepted = "dispatch_accepted";
}

public static class StudioMemberBindingRunRoleNames
{
    public const string Candidate = "candidate";
}

public static class StudioMemberInvocationReadinessStatusNames
{
    public const string Ready = "ready";
    public const string ServiceCatalogMissing = "service_catalog_missing";
    public const string ServingSetMissing = "serving_set_missing";
    public const string EligibleServingTargetMissing = "eligible_serving_target_missing";
    public const string ServiceCatalogTargetMissing = "service_catalog_target_missing";
    public const string TrafficViewTargetMissing = "traffic_view_target_missing";
    public const string PreparedArtifactMissing = "prepared_artifact_missing";
    public const string Unknown = "unknown";
}

public sealed record StudioMemberInvocationReadinessResponse(
    bool CanInvoke,
    string Status,
    string ReasonCode,
    string Message,
    string? RevisionId = null,
    string? DeploymentId = null,
    DateTimeOffset? ObservedAtUtc = null);

/// <summary>
/// Wire-format status values returned in
/// <see cref="StudioMemberBindingRevisionActionResponse.Status"/>. Centralizing
/// the literal lets future lifecycle actions (e.g. "deprecated") declare
/// themselves alongside <see cref="Retired"/> instead of rotting as a magic
/// string scattered across handler bodies.
/// </summary>
public static class MemberRevisionLifecycleStatusNames
{
    public const string Retired = "retired";
}

/// <summary>
/// Implementation reference returned to the caller. Always typed — never a
/// generic property bag — so the frontend can dispatch on
/// <see cref="ImplementationKind"/> without parsing arbitrary keys.
/// </summary>
public sealed record StudioMemberImplementationRefResponse(
    string ImplementationKind,
    string? WorkflowId = null,
    string? WorkflowRevision = null,
    string? ScriptId = null,
    string? ScriptRevision = null,
    string? DiagnosticActorTypeName = null);

public sealed record StudioMemberSummaryResponse(
    string MemberId,
    string ScopeId,
    string DisplayName,
    string Description,
    string ImplementationKind,
    string LifecycleStage,
    string PublishedServiceId,
    string? LastBoundRevisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Optional team assignment (ADR-0017). Null means the member is not
    /// currently in any team. Added as a non-positional <c>init</c> property
    /// so existing callers that construct the record positionally are not
    /// broken; the query port populates it from the read model document and
    /// the create/patch flows pass it through unchanged.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Typed implementation identity shared by scope and team roster
    /// summaries. Null means the member read model has not yet observed an
    /// implementation reference.
    /// </summary>
    public StudioMemberImplementationRefResponse? ImplementationRef { get; init; }
}

public sealed record StudioMemberDetailResponse(
    StudioMemberSummaryResponse Summary,
    StudioMemberImplementationRefResponse? ImplementationRef,
    StudioMemberBindingContractResponse? LastBinding)
{
    public StudioMemberBindingRunStatusResponse? CurrentBindingRun { get; init; }
}

public sealed record StudioMemberBindingContractResponse(
    string PublishedServiceId,
    string RevisionId,
    string ImplementationKind,
    DateTimeOffset BoundAt,
    string? ExpectedActorId = null);

public sealed record StudioMemberPublishedBindingRecordRequest(
    string PublishedServiceId,
    string RevisionId,
    string ImplementationKind,
    StudioMemberImplementationRefResponse ImplementationRef,
    string? ExpectedActorId = null);

/// <summary>
/// Wrapper returned from <c>GET /members/{memberId}/binding</c> so the
/// response is always a JSON object — distinguishes "exists but never
/// bound" (<see cref="LastBinding"/> is <c>null</c>, status 200) from
/// "member missing" (typed 404 STUDIO_MEMBER_NOT_FOUND).
/// </summary>
public sealed record StudioMemberBindingViewResponse(
    StudioMemberBindingContractResponse? LastBinding)
{
    public StudioMemberBindingRunStatusResponse? CurrentBindingRun { get; init; }
}

public sealed record StudioMemberBindingFailureResponse(
    string Code,
    string Message,
    DateTimeOffset FailedAt);

public sealed record StudioMemberBindingRunResultResponse(
    string PublishedServiceId,
    string RevisionId,
    string ImplementationKind,
    string? ExpectedActorId = null);

// Refactor (iter159/cluster-594-first):
//   Old pattern: Studio member binding-run status response did not expose StateVersion.
//   New principle: expose the read model's StateVersion so the frontend can show
//   not-yet-materialized state with an honest freshness marker.
public sealed record StudioMemberBindingRunStatusResponse(
    string BindingRunId,
    string ScopeId,
    string MemberId,
    string Status,
    long StateVersion,
    StudioMemberBindingFailureResponse? Failure = null,
    DateTimeOffset? UpdatedAt = null)
{
    public string? PlatformBindingCommandId { get; init; }

    public StudioMemberBindingRunResultResponse? Result { get; init; }
}

public sealed record StudioMemberRosterResponse(
    string ScopeId,
    IReadOnlyList<StudioMemberSummaryResponse> Members,
    string? NextPageToken = null);

// Refactor (iter74/cluster-074-studio-team-members-query-fanout):
//   Old pattern: Host loops scope roster pages + Host-side TeamId filter
//   New principle: ReadModel query port owns scope_id+team_id filter before pagination
public sealed record StudioMemberRosterPageRequest(
    int? PageSize = null,
    string? PageToken = null,
    string? TeamId = null);

public sealed record CreateStudioMemberRequest(
    string DisplayName,
    string ImplementationKind,
    string? Description = null,
    string? MemberId = null,
    // Initial team assignment. Required for workflow members; optional for
    // other member kinds. Empty string is rejected at the application boundary;
    // null / absent means "do not assign" only for non-workflow members.
    string? TeamId = null,
    StudioMemberImplementationRefResponse? ImplementationRef = null);

public sealed class StudioMemberCreateImplementationRefNotAllowedException : InvalidOperationException
{
    public const string ErrorCode = "STUDIO_MEMBER_CREATE_IMPLEMENTATION_REF_NOT_ALLOWED";
    public const string FieldName = "implementationRef";
    public const string BindingRouteTemplate = "PUT /api/scopes/{scopeId}/members/{memberId}/binding";

    public StudioMemberCreateImplementationRefNotAllowedException(string scopeId)
        : base(
            "implementationRef is not accepted when creating a studio member. " +
            "Omit implementationRef, create the member shell, then bind the implementation through " +
            BindingRouteTemplate + ".")
    {
        ScopeId = scopeId;
    }

    public string ScopeId { get; }

    public string Field => FieldName;
}

/// <summary>
/// Wire body for <c>PATCH /api/scopes/{scopeId}/members/{memberId}</c> when
/// the caller wants to change the member's team assignment (ADR-0017 §Q6).
/// Uses <see cref="PatchValue{T}"/> so the application layer can distinguish
/// "absent" (no change) from "explicit null" (unassign) without a sentinel
/// empty-string value reaching the actor.
/// </summary>
public sealed record UpdateStudioMemberRequest(
    PatchValue<string> DisplayName = default,
    PatchValue<string> TeamId = default,
    PatchValue<StudioMemberImplementationRefResponse> ImplementationRef = default);

public static class StudioMemberCommandStatusNames
{
    public const string Accepted = "accepted";
    public const string NoChange = "no_change";
    public const string DeleteAccepted = "delete_accepted";
}

public sealed record StudioMemberCommandResponse(
    string Status,
    string ScopeId,
    string MemberId,
    DateTimeOffset? AckedAt = null);

/// <summary>
/// Centralized input bounds applied at the create boundary so a single
/// request cannot push 10MB of displayName / description / memberId all the
/// way through to the actor state and read model. Slug pattern on
/// memberId keeps caller-supplied ids URL-safe and free of separators that
/// the actor-id convention reserves.
/// </summary>
public static class StudioMemberInputLimits
{
    public const int MaxDisplayNameLength = 256;
    public const int MaxDescriptionLength = 2048;
    public const int MaxMemberIdLength = 64;

    public static readonly System.Text.RegularExpressions.Regex MemberIdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9_\-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);
}

public sealed record UpdateStudioMemberBindingRequest(
    string? RevisionId = null,
    StudioMemberWorkflowBindingSpec? Workflow = null,
    StudioMemberScriptBindingSpec? Script = null,
    StudioMemberGAgentBindingSpec? GAgent = null)
{
    public IReadOnlyList<NyxIdExplicitRequestConfirmationInput>? ExplicitRequestConfirmations { get; init; }

    [JsonIgnore]
    public WorkflowCapabilityAdmissionContext? CapabilityAdmission { get; init; }
}

public sealed record StudioMemberWorkflowBindingSpec
{
    [JsonConstructor]
    public StudioMemberWorkflowBindingSpec(
        string WorkflowId,
        IReadOnlyList<string> WorkflowYamls)
    {
        this.WorkflowId = WorkflowId;
        this.WorkflowYamls = WorkflowYamls;
    }

    public StudioMemberWorkflowBindingSpec(IReadOnlyList<string> WorkflowYamls)
        : this(string.Empty, WorkflowYamls)
    {
    }

    public string WorkflowId { get; init; }

    public IReadOnlyList<string> WorkflowYamls { get; init; }

    [JsonIgnore]
    public WorkflowCapabilityAdmissionPlan? CapabilityAdmissionPlan { get; init; }
}

public sealed record StudioMemberScriptBindingSpec(
    string ScriptId,
    string? ScriptRevision = null);

public sealed record StudioMemberGAgentEndpointSpec(
    string EndpointId,
    string DisplayName,
    string Kind,
    string RequestTypeUrl,
    string ResponseTypeUrl,
    string? Description = null);

public sealed record StudioMemberGAgentBindingSpec(
    string AgentKind,
    IReadOnlyList<StudioMemberGAgentEndpointSpec>? Endpoints = null);

public sealed record StudioMemberBindingAcceptedResponse(
    string Status,
    string BindingRunId,
    string ScopeId,
    string MemberId)
{
    public string AckStage { get; init; } = StudioMemberBindingAckStageNames.DispatchAccepted;

    public string BindingRunRole { get; init; } = StudioMemberBindingRunRoleNames.Candidate;
}

public sealed record StudioMemberBindingRunStartRequest(
    string BindingRunId,
    string ScopeId,
    string MemberId,
    string ImplementationKind,
    UpdateStudioMemberBindingRequest Binding);

/// <summary>
/// Member-first endpoint contract. Mirrors the existing scope-default
/// <c>ScopeServiceEndpointContractHttpResponse</c> shape so the frontend can
/// keep its rendering, while pinning <see cref="MemberId"/> and exposing the
/// member-first <see cref="InvokePath"/>. <see cref="PublishedServiceId"/> is
/// included for parity with the legacy serviceId-based payload, not as a
/// required field for the caller.
/// </summary>
public sealed record StudioMemberEndpointContractResponse(
    string ScopeId,
    string MemberId,
    string PublishedServiceId,
    string EndpointId,
    string InvokePath,
    string Method,
    string RequestContentType,
    string ResponseContentType,
    string RequestTypeUrl,
    string ResponseTypeUrl,
    bool SupportsSse,
    bool SupportsWebSocket,
    bool SupportsAguiFrames,
    string? StreamFrameFormat,
    bool SmokeTestSupported,
    string DefaultSmokeInputMode,
    string? DefaultSmokePrompt,
    string? SampleRequestJson,
    string DeploymentStatus,
    string RevisionId,
    StudioMemberInvocationReadinessResponse InvocationReadiness,
    string? CurlExample = null,
    string? FetchExample = null);

/// <summary>
/// Activation result for a member's binding revision. Carries
/// <see cref="MemberId"/> as the stable identity; <see cref="PublishedServiceId"/>
/// is included so the frontend can fall back to the legacy serviceId-keyed
/// store while it migrates, but no caller should require it.
/// </summary>
public sealed record StudioMemberBindingActivationResponse(
    string ScopeId,
    string MemberId,
    string PublishedServiceId,
    string DisplayName,
    string RevisionId);

/// <summary>
/// Generic member-first revision lifecycle action result, mirroring the
/// legacy <c>ScopeServiceRevisionActionHttpResponse</c>. <see cref="Status"/>
/// is the lowercase verb (e.g. <c>retired</c>) the legacy payload uses.
/// </summary>
public sealed record StudioMemberBindingRevisionActionResponse(
    string ScopeId,
    string MemberId,
    string PublishedServiceId,
    string RevisionId,
    string Status);
