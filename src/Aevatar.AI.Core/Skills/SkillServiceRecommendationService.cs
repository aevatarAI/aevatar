using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions.Skills;

namespace Aevatar.AI.Core.Skills;

// Stateless discovery over caller-visible external facts. Suggestions are not grants
// or persisted dependencies; channel authorization still requires explicit exact IDs.
public sealed class SkillServiceRecommendationService(ISkillServiceDiscoverySource source)
    : ISkillServiceRecommendationService
{
    public async Task<SkillServiceRecommendations> RecommendAsync(
        string accessToken, string skillName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Caller credentials are required.", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(skillName) || skillName.Length > 128 ||
            skillName != skillName.Trim() || skillName is "." or "..")
            throw new ArgumentException("Invalid skill name.", nameof(skillName));

        var input = await source.ReadAsync(accessToken, skillName, ct);
        if (!string.Equals(input.SkillName, skillName, StringComparison.Ordinal))
            throw new SkillServiceDiscoveryException();

        var candidates = input.Catalog.ToDictionary(entry => entry.Slug, StringComparer.Ordinal);
        foreach (var instance in input.Instances)
            candidates.TryAdd(instance.Slug, new SkillServiceCatalogEntry
            {
                Slug = instance.Slug,
                Name = instance.Label,
            });
        if (input.LinkedServiceSlug.Length > 0)
            candidates.TryAdd(input.LinkedServiceSlug, new SkillServiceCatalogEntry
            {
                Slug = input.LinkedServiceSlug,
                Name = input.LinkedServiceSlug,
            });

        var result = new SkillServiceRecommendations { SkillName = skillName };
        var text = input.Description + "\n" + input.Instructions;
        foreach (var entry in candidates.Values)
        {
            ct.ThrowIfCancellationRequested();
            var evidence = entry.Slug == input.LinkedServiceSlug
                ? SkillServiceEvidence.Linked
                : entry.RecommendedSkillNames.Contains(skillName)
                    ? SkillServiceEvidence.Catalog
                    : Mentions(text, entry.Slug) || Mentions(text, entry.Name)
                        ? SkillServiceEvidence.Mention
                        : SkillServiceEvidence.Unspecified;
            if (evidence == SkillServiceEvidence.Unspecified)
                continue;
            var suggestion = new SkillServiceRecommendation
            {
                Slug = entry.Slug,
                Label = entry.Name,
                Evidence = evidence,
            };
            suggestion.Instances.Add(input.Instances
                .Where(instance => instance.Slug == entry.Slug)
                .Select(instance => instance.Clone()));
            result.Suggestions.Add(suggestion);
        }
        return result;
    }

    private static bool Mentions(string text, string term) =>
        term.Trim().Length >= 3 && Regex.IsMatch(
            text,
            @"(?<![\p{L}\p{N}_-])" + Regex.Escape(term) + @"(?![\p{L}\p{N}_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
}
