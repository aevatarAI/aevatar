using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Caller-scoped read port for the user-agent catalog. The contract has no method that
/// returns un-scoped agent data; every query carries the caller's <see cref="OwnerScope"/>.
/// Owner-only methods apply strict owner equality, while visible/triggerable methods also
/// admit catalog-owned sharing grants for the caller's channel registration scope.
/// <c>GetForCallerAsync</c> returns <c>null</c> for both "doesn't exist" and
/// "exists but not yours" - single semantic, no existence/version disclosure to non-owners
/// (issue #466).
///
/// The DTO returned (<see cref="UserAgentCatalogReadModelEntry"/>) does not surface
/// <c>NyxApiKey</c>; that secret is only readable through the narrow internal
/// <see cref="IUserAgentDeliveryTargetReader"/> registered for outbound delivery code,
/// not for LLM tools.
/// </summary>
public interface IUserAgentCatalogQueryPort
{
    Task<UserAgentCatalogReadModelEntry?> GetForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default);

    Task<UserAgentCatalogReadModelEntry?> GetVisibleForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default);

    Task<UserAgentCatalogReadModelEntry?> GetTriggerableForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default);

    /// <summary>
    /// Returns whether the catalog id is occupied by any non-tombstoned row. This is a
    /// narrow create-admission check: callers get only availability, never another
    /// owner's catalog data.
    /// </summary>
    Task<bool> ExistsActiveAsync(string agentId, CancellationToken ct = default);

    Task<IReadOnlyList<UserAgentCatalogReadModelEntry>> QueryByCallerAsync(OwnerScope caller, CancellationToken ct = default);

    Task<IReadOnlyList<UserAgentCatalogReadModelEntry>> QueryVisibleByCallerAsync(OwnerScope caller, CancellationToken ct = default);

    Task<IReadOnlyList<UserAgentApiKeyRevocationReadModelEntry>> QueryPendingApiKeyRevocationsByCallerAsync(OwnerScope caller, CancellationToken ct = default);

    /// <summary>
    /// Returns the projected state version for an agent the caller owns; <c>null</c> when
    /// the agent does not exist OR the caller does not own it. Both conditions collapse
    /// to <c>null</c> so a non-owner cannot probe existence/version progression.
    /// </summary>
    Task<long?> GetStateVersionForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default);
}
