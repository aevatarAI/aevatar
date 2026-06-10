namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Fetches remote skill definitions from a platform such as Ornn.
/// </summary>
public interface IRemoteSkillFetcher
{
    /// <summary>
    /// Fetches a skill definition by name or id.
    /// </summary>
    /// <param name="accessToken">Caller credential used by the remote platform.</param>
    /// <param name="nameOrId">Skill name or id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The skill definition, or null when the skill was not found.</returns>
    Task<SkillDefinition?> FetchSkillAsync(string accessToken, string nameOrId, CancellationToken ct = default);
}
