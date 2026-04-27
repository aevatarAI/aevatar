namespace Aevatar.Studio.Application.Studio.Contracts;

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
    string? ActorTypeName = null);

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
    DateTimeOffset UpdatedAt);

public sealed record StudioMemberDetailResponse(
    StudioMemberSummaryResponse Summary,
    StudioMemberImplementationRefResponse? ImplementationRef,
    StudioMemberBindingContractResponse? LastBinding);

public sealed record StudioMemberBindingContractResponse(
    string PublishedServiceId,
    string RevisionId,
    string ImplementationKind,
    DateTimeOffset BoundAt);

/// <summary>
/// Wrapper returned from <c>GET /members/{memberId}/binding</c> so the
/// response is always a JSON object — distinguishes "exists but never
/// bound" (<see cref="LastBinding"/> is <c>null</c>, status 200) from
/// "member missing" (typed 404 STUDIO_MEMBER_NOT_FOUND).
/// </summary>
public sealed record StudioMemberBindingViewResponse(
    StudioMemberBindingContractResponse? LastBinding);

public sealed record StudioMemberRosterResponse(
    string ScopeId,
    IReadOnlyList<StudioMemberSummaryResponse> Members,
    string? NextPageToken = null);

public sealed record StudioMemberRosterPageRequest(
    int? PageSize = null,
    string? PageToken = null);

public sealed record CreateStudioMemberRequest(
    string DisplayName,
    string ImplementationKind,
    string? Description = null,
    string? MemberId = null);

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
    StudioMemberGAgentBindingSpec? GAgent = null);

public sealed record StudioMemberWorkflowBindingSpec(
    IReadOnlyList<string> WorkflowYamls);

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
    string ActorTypeName,
    IReadOnlyList<StudioMemberGAgentEndpointSpec>? Endpoints = null);

public sealed record StudioMemberBindingResponse(
    string MemberId,
    string PublishedServiceId,
    string RevisionId,
    string ImplementationKind,
    string ScopeId,
    string ExpectedActorId);
