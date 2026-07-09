using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Application-level facade for member-first Studio APIs. Orchestrates the
/// command and query ports plus the underlying scope binding capability so
/// the HTTP layer never has to know about ServiceId or scope-default
/// fallback. Endpoints depend on this interface rather than reaching for
/// <see cref="IStudioMemberCommandPort"/>, <see cref="IStudioMemberQueryPort"/>
/// or the platform binding port directly.
/// </summary>
public interface IStudioMemberService
{
    Task<StudioMemberSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioMemberRequest request,
        CancellationToken ct = default);

    Task<StudioMemberRosterResponse> ListAsync(
        string scopeId,
        StudioMemberRosterPageRequest? page = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the member detail. Throws
    /// <see cref="StudioMemberNotFoundException"/> when no member with
    /// the given id exists in the scope — endpoints map this to 404
    /// <c>STUDIO_MEMBER_NOT_FOUND</c>, the same body every member-centric
    /// endpoint returns for missing-member.
    /// </summary>
    Task<StudioMemberDetailResponse> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default);

    /// <summary>
    /// Accepts a binding request for asynchronous actor-owned execution.
    /// The returned receipt only means the command was dispatched with a
    /// stable binding run id; admission and platform completion are observed
    /// later through binding run status queries.
    /// </summary>
    Task<StudioMemberBindingAcceptedResponse> BindAsync(
        string scopeId,
        string memberId,
        UpdateStudioMemberBindingRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the last successful binding contract for the member, or
    /// <c>null</c> when the member exists but has never been bound.
    /// Throws <see cref="StudioMemberNotFoundException"/> when the member
    /// itself does not exist — endpoints distinguish "missing member" (404)
    /// from "exists, never bound" (200 with null binding).
    /// </summary>
    Task<StudioMemberBindingViewResponse> GetBindingAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default);

    Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
        string scopeId,
        string memberId,
        string bindingRunId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the request/response contract for a single endpoint on the
    /// member-owned published service. Resolves the member's
    /// <c>publishedServiceId</c> internally so callers never pass a serviceId.
    /// Returns <c>null</c> when the member is bound but no matching endpoint
    /// exists; throws <see cref="StudioMemberNotFoundException"/> when the
    /// member itself does not exist; throws
    /// <see cref="InvalidOperationException"/> when the member exists but has
    /// not been bound yet (no published service to read a contract from).
    /// </summary>
    Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
        string scopeId,
        string memberId,
        string endpointId,
        CancellationToken ct = default);

    /// <summary>
    /// Activates a binding revision on the member's published service:
    /// sets the revision as default-serving and marks it as the active
    /// service revision. Resolves the member-owned
    /// <c>publishedServiceId</c> internally; callers never pass a serviceId.
    /// Throws <see cref="StudioMemberNotFoundException"/> when the member
    /// is missing, and <see cref="InvalidOperationException"/> when the
    /// member exists but has not been bound, or when the requested revision
    /// is missing/retired.
    /// </summary>
    Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
        string scopeId,
        string memberId,
        string revisionId,
        CancellationToken ct = default);

    /// <summary>
    /// Retires a binding revision on the member's published service.
    /// Resolves the member-owned <c>publishedServiceId</c> internally;
    /// callers never pass a serviceId.
    /// Throws <see cref="StudioMemberNotFoundException"/> when the member
    /// is missing, and <see cref="InvalidOperationException"/> when the
    /// member is unbound or the revision does not exist.
    /// </summary>
    Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
        string scopeId,
        string memberId,
        string revisionId,
        CancellationToken ct = default);

    /// <summary>
    /// Patches a member's properties. Merge Patch semantics: absent fields mean
    /// "no change"; explicit null is accepted only for nullable fields such as
    /// <c>teamId</c>. The returned receipt means the command intent was
    /// accepted for asynchronous actor-owned execution; callers should use the
    /// member query resource to observe read-model materialization.
    /// </summary>
    Task<StudioMemberCommandResponse> UpdateAsync(
        string scopeId,
        string memberId,
        UpdateStudioMemberRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Physically deletes the Studio member authority by tombstoning the
    /// member actor. The delete is idempotent for already-deleted members that
    /// still have actor state; never-created / unknown members return
    /// <see cref="StudioMemberNotFoundException"/>. Published service artifacts
    /// and revisions are intentionally left untouched.
    /// </summary>
    Task<StudioMemberCommandResponse> DeleteAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default);
}
