namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public sealed class NyxIdConnectedServiceInventoryReader
{
    private readonly NyxIdServiceInstanceClient _client;

    public NyxIdConnectedServiceInventoryReader(NyxIdServiceInstanceClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<NyxIdServiceInventoryResult> ReadAsync(
        string userToken,
        string? organizationToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userToken);
        var bindings = await _client
            .DiscoverAsync(userToken, organizationToken, ct)
            .ConfigureAwait(false);
        return ToInventoryResult(bindings);
    }

    public async Task<NyxIdServiceInventoryResult> ReadAgentKeyAsync(
        string agentKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        var bindings = await _client
            .DiscoverAgentKeyAsync(agentKey, ct)
            .ConfigureAwait(false);
        return ToInventoryResult(bindings);
    }

    private static NyxIdServiceInventoryResult ToInventoryResult(
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings)
    {
        var instances = bindings
            .Where(static binding => binding.Instance.IsActive && binding.Instance.CredentialAllowed)
            .Select(static binding => binding.Instance.Clone())
            .ToArray();

        var result = new NyxIdServiceInventoryResult();
        result.Instances.Add(instances);
        result.RecommendedSkillCatalog.Add(instances.SelectMany(BuildRecommendedSkillCatalogEntries));
        return result;
    }

    private static IEnumerable<NyxIdRecommendedSkillCatalogEntry> BuildRecommendedSkillCatalogEntries(
        NyxIdServiceInstance instance)
    {
        foreach (var skillRef in instance.RecommendedSkillRefs)
        {
            var title = FirstNonEmpty(skillRef.DisplayName, skillRef.RecommendationName, skillRef.SkillId);
            yield return new NyxIdRecommendedSkillCatalogEntry
            {
                UserServiceId = instance.UserServiceId,
                ServiceSlug = instance.DisplaySlug,
                ServiceLabel = FirstNonEmpty(instance.Label, instance.DisplaySlug, instance.CatalogServiceSlug),
                SkillRef = skillRef.Clone(),
                Title = title,
                TaskSummary = $"Load this recommended skill for {FirstNonEmpty(instance.Label, instance.DisplaySlug, instance.CatalogServiceSlug)} connected-service tasks related to {title}.",
            };
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
