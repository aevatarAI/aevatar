using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.AI.ToolProviders.Ornn;

public sealed class OrnnRemoteSkillDiscovery : IRemoteSkillDiscovery
{
    private readonly OrnnSkillClient _client;

    public OrnnRemoteSkillDiscovery(OrnnSkillClient client) => _client = client;

    public async Task<IReadOnlyList<RemoteSkillSummary>> SearchSkillsAsync(
        RemoteSkillSearchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccessToken) || string.IsNullOrWhiteSpace(request.Query))
            return [];

        var result = await _client.SearchSkillsAsync(
            request.AccessToken,
            request.Query,
            request.Scope,
            page: 1,
            pageSize: request.PageSize,
            mode: request.Mode,
            ct: ct);

        if (!string.IsNullOrWhiteSpace(result.Error) || result.Items.Count == 0)
            return [];

        return result.Items
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
            .Select(skill => new RemoteSkillSummary(
                Name: skill.Name!.Trim(),
                Description: skill.Description?.Trim() ?? string.Empty,
                RemoteId: string.IsNullOrWhiteSpace(skill.Guid) ? null : skill.Guid.Trim(),
                IsPrivate: skill.IsPrivate,
                Category: skill.Metadata?.Category,
                Tags: skill.Tags ?? skill.Metadata?.Tags ?? []))
            .ToArray();
    }
}
