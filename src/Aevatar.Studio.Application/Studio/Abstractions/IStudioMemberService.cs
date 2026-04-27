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
    /// Binds the given member to its own stable <c>publishedServiceId</c>
    /// (never the scope default service). Resolves the member, builds a
    /// scope binding request with <c>ServiceId = publishedServiceId</c>,
    /// delegates to the existing scope binding command port, and records the
    /// resulting revision back on the member authority.
    /// </summary>
    Task<StudioMemberBindingResponse> BindAsync(
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
    Task<StudioMemberBindingContractResponse?> GetBindingAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default);
}
