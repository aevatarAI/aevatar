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

public sealed record StudioMemberRosterResponse(
    string ScopeId,
    IReadOnlyList<StudioMemberSummaryResponse> Members);

public sealed record CreateStudioMemberRequest(
    string DisplayName,
    string ImplementationKind,
    string? Description = null,
    string? MemberId = null);

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
