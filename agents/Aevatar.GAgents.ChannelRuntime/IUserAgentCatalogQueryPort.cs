namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Caller-scoped read port for the user-agent catalog. The contract has no method that
/// returns un-scoped agent data; every query carries the caller's <see cref="OwnerScope"/>
/// and the implementation pushes a strict full-tuple equality filter into the projection
/// reader. <c>GetForCallerAsync</c> returns <c>null</c> for both "doesn't exist" and
/// "exists but not yours" — single semantic, no existence/version disclosure to non-owners
/// (issue #466).
///
/// The DTO returned (<see cref="UserAgentCatalogEntry"/>) does not surface
/// <c>NyxApiKey</c>; that secret is only readable through the narrow internal
/// <see cref="IUserAgentDeliveryTargetReader"/> registered for outbound delivery code,
/// not for LLM tools.
/// </summary>
public interface IUserAgentCatalogQueryPort
{
    Task<UserAgentCatalogEntry?> GetForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default);

    Task<IReadOnlyList<UserAgentCatalogEntry>> QueryByCallerAsync(OwnerScope caller, CancellationToken ct = default);

    /// <summary>
    /// Returns the projected state version for an agent the caller owns; <c>null</c> when
    /// the agent does not exist OR the caller does not own it. Both conditions collapse
    /// to <c>null</c> so a non-owner cannot probe existence/version progression.
    /// </summary>
    Task<long?> GetStateVersionForCallerAsync(string agentId, OwnerScope caller, CancellationToken ct = default);
}
