namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Resolves the request-local access token used to read one remote skill.
/// Implementations must not persist or cache the returned bearer token.
/// </summary>
public interface IRemoteSkillAccessTokenResolver
{
    /// <summary>
    /// Returns a transient token for the current caller, or <see langword="null"/>
    /// when remote-skill access is unavailable for this request.
    /// </summary>
    Task<string?> ResolveAsync(string skillName, CancellationToken ct = default);
}
