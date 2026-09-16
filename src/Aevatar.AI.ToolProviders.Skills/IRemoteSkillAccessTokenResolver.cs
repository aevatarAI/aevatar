namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Resolves the request-local access token used to read one remote skill.
/// Implementations must not persist or cache the returned bearer token.
/// </summary>
public interface IRemoteSkillAccessTokenResolver
{
    /// <summary>
    /// Returns a transient token resolution for the current caller. A failed
    /// resolution carries a typed <see cref="RemoteSkillAccessTokenFailureKind"/>
    /// so callers can surface actionable guidance instead of a generic error.
    /// </summary>
    Task<RemoteSkillAccessTokenResolution> ResolveAsync(string skillName, CancellationToken ct = default);
}
