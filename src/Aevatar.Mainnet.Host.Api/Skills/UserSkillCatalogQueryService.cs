using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.Mainnet.Host.Api.Skills;

// Composes the Ornn skill client into the host-side skills catalog read surface. Thin by design: the
// endpoint depends on IUserSkillCatalogQueryService (business-named), not the raw infra client.
internal sealed class UserSkillCatalogQueryService : IUserSkillCatalogQueryService
{
    private readonly OrnnSkillClient _ornnClient;
    private readonly IRemoteSkillFetcher _remoteSkillFetcher;

    public UserSkillCatalogQueryService(OrnnSkillClient ornnClient, IRemoteSkillFetcher remoteSkillFetcher)
    {
        _ornnClient = ornnClient ?? throw new ArgumentNullException(nameof(ornnClient));
        _remoteSkillFetcher = remoteSkillFetcher ?? throw new ArgumentNullException(nameof(remoteSkillFetcher));
    }

    public async Task<UserSkillDetail?> GetSkillAsync(
        string accessToken,
        string guid,
        CancellationToken ct = default)
    {
        var skill = await _remoteSkillFetcher.FetchSkillAsync(accessToken, guid, ct);
        if (skill == null)
            return null;

        // runKind drives the page's invoke contract: a skill carrying workflow YAML runs that workflow;
        // otherwise it runs a synthesized single llm_call (see UserSkillRunService).
        var runKind = skill.Workflows.Count > 0 ? "workflow" : "direct";
        return new UserSkillDetail(
            Guid: guid,
            Name: skill.Name,
            Description: skill.Description,
            RunKind: runKind,
            WhenToUse: skill.WhenToUse ?? string.Empty,
            Arguments: skill.Arguments ?? string.Empty,
            Inputs: []);
    }

    public async Task<UserSkillListResult> ListVisibleSkillsAsync(
        string accessToken,
        string query,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        // "mixed" = the caller's full accessible set (public + private + shared). Visibility is bounded by the
        // caller's own NyxID access token routed through the Ornn proxy, so a caller only ever sees skills they
        // could actually invoke.
        var result = await _ornnClient.SearchSkillsAsync(
            accessToken,
            query ?? string.Empty,
            scope: "mixed",
            page: page,
            pageSize: pageSize,
            ct: ct);

        return UserSkillCatalogMapper.ToResult(result);
    }
}

internal static class UserSkillCatalogMapper
{
    public static UserSkillListResult ToResult(OrnnSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new UserSkillListResult(
            result.Items.Select(ToSummary).ToList(),
            result.Total,
            result.Page,
            result.PageSize,
            string.IsNullOrEmpty(result.Error) ? null : result.Error);
    }

    private static UserSkillSummary ToSummary(OrnnSkillSummary item) =>
        new(
            item.Guid ?? string.Empty,
            item.Name ?? string.Empty,
            item.Description ?? string.Empty,
            item.Metadata?.Category ?? string.Empty,
            (item.Tags ?? item.Metadata?.Tags ?? []).ToList(),
            item.IsPrivate);
}
