namespace Aevatar.AI.ToolProviders.Skills;

public sealed record RemoteSkillSearchRequest(
    string AccessToken,
    string Query,
    string Scope = "mixed",
    string Mode = "semantic",
    int PageSize = 2);

public sealed record RemoteSkillSummary(
    string Name,
    string Description,
    string? RemoteId = null,
    bool IsPrivate = false,
    string? Category = null,
    IReadOnlyList<string>? Tags = null);

public interface IRemoteSkillDiscovery
{
    Task<IReadOnlyList<RemoteSkillSummary>> SearchSkillsAsync(
        RemoteSkillSearchRequest request,
        CancellationToken ct = default);
}
