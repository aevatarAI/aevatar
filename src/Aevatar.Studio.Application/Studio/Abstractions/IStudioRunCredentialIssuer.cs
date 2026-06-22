namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Mints a <b>durable</b> bearer credential for a provisioned scheduled workflow
/// run. A C1-provisioned run is produced by a scheduled-dispatch, which must carry
/// a credential that authenticates the run's LLM call at fire time. A raw nyxid
/// caller has no re-mintable NyxID OAuth binding, so the scheduled-dispatch subject
/// token-exchange cannot mint one — this port provides the durable credential
/// instead (mirroring the SkillRunner scheduled-agent pattern that mints a durable
/// NyxID agent key under the caller's account).
///
/// Implementations are optional: when none is registered, the provisioning service
/// falls back to threading the caller's forwarded bearer token directly (valid for
/// a soon-firing one-shot demo run; a recurring monitor benefits from a minted
/// durable key that outlives the caller's session token).
///
/// The caller bearer token is always an input parameter — implementations hold no
/// ambient identity.
/// </summary>
public interface IStudioRunCredentialIssuer
{
    /// <summary>
    /// Mints a durable bearer credential for the run owned by <paramref name="agentId"/>
    /// under <paramref name="scopeId"/>, authorized via the caller
    /// <paramref name="callerBearerToken"/>. Returns the minted bearer token, or
    /// <c>null</c> when a durable credential could not be minted (the caller should
    /// fall back to the forwarded token).
    /// </summary>
    Task<string?> IssueDurableRunCredentialAsync(
        string callerBearerToken,
        string agentId,
        string scopeId,
        CancellationToken ct = default);
}
