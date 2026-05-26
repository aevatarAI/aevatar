// ─────────────────────────────────────────────────────────────
// OrnnRemoteSkillFetcher - loads skills from Ornn and maps Ornn API responses to SkillDefinition.
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>
/// Remote Ornn skill fetcher that retrieves skill packages through <see cref="OrnnSkillClient"/>.
/// </summary>
public sealed class OrnnRemoteSkillFetcher : IRemoteSkillFetcher
{
    private readonly OrnnSkillClient _client;

    public OrnnRemoteSkillFetcher(OrnnSkillClient client) => _client = client;

    public async Task<SkillDefinition?> FetchSkillAsync(
        string accessToken, string nameOrId, CancellationToken ct = default)
    {
        var skill = await _client.GetSkillJsonAsync(accessToken, nameOrId, ct);
        if (skill == null)
            return null;

        // Extract instructions from SKILL.md when the package includes it.
        var instructions = "";
        Dictionary<string, string>? associatedFiles = null;

        if (skill.Files != null && skill.Files.Count > 0)
        {
            if (skill.Files.TryGetValue("SKILL.md", out var skillMd))
                instructions = skillMd;

            var others = skill.Files
                .Where(f => !f.Key.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value);

            if (others.Count > 0)
                associatedFiles = others;
        }

        // Parse optional frontmatter from the instruction body.
        var parser = new SkillFrontmatterParser();
        var parsed = parser.Parse(instructions);

        return new SkillDefinition
        {
            Name = parsed.Name ?? skill.Name ?? nameOrId,
            Description = parsed.Description ?? skill.Description ?? "",
            Instructions = parsed.Body,
            Source = SkillSource.Remote,
            RemoteId = nameOrId,
            Arguments = parsed.Arguments,
            WhenToUse = parsed.WhenToUse,
            IsModelInvocable = parsed.IsModelInvocable,
            IsUserInvocable = parsed.IsUserInvocable,
            AssociatedFiles = associatedFiles,
        };
    }
}
