using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Application-level facade for member-first Studio APIs. Orchestrates the
/// command and query ports so the HTTP layer never has to know about
/// ServiceId or scope-default fallback. Endpoints depend on this interface
/// rather than reaching for <see cref="IStudioMemberCommandPort"/> or
/// <see cref="IStudioMemberQueryPort"/> directly.
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
    /// Requests an asynchronous bind on the member authority. Synchronous
    /// return means accepted for dispatch with a stable binding id; completion
    /// is observed later through the member read model.
    /// </summary>
    Task<StudioMemberBindingAcceptedResponse> BindAsync(
        string scopeId,
        string memberId,
        UpdateStudioMemberBindingRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the last successful binding contract plus the latest bind run
    /// status for the member.
    /// Throws <see cref="StudioMemberNotFoundException"/> when the member
    /// itself does not exist — endpoints distinguish "missing member" (404)
    /// from "exists, never bound" (200 with null binding).
    /// </summary>
    Task<StudioMemberBindingViewResponse> GetBindingAsync(
        string scopeId,
        string memberId,
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
    /// Patches a member's properties — currently the team assignment
    /// (ADR-0017 §Q6). Merge Patch semantics: <c>request.TeamId</c> absent
    /// means "no change", <c>request.TeamId.Value == null</c> means
    /// "unassign", and a non-empty value means "assign / reassign".
    /// Returns the updated member detail.
    /// </summary>
    Task<StudioMemberDetailResponse> UpdateAsync(
        string scopeId,
        string memberId,
        UpdateStudioMemberRequest request,
        CancellationToken ct = default);
}
