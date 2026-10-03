namespace Aevatar.GAgentService.Abstractions.CatalogSkills;

public interface ICatalogRecommendedSkillUpdateApplicationService
{
    Task<CatalogRecommendedSkillUpdateOutcome> UpdateAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillUpdateExecutionCredential credential,
        CancellationToken ct = default);
}

/// <summary>
/// The administrator bearer for one catalog recommended-skill update request.
/// This request-scoped value is intentionally outside the protobuf contracts and
/// must never be persisted, logged, or copied into an update outcome.
/// </summary>
public sealed class CatalogRecommendedSkillUpdateExecutionCredential
{
    public CatalogRecommendedSkillUpdateExecutionCredential(string bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("A bearer token is required.", nameof(bearerToken));

        BearerToken = bearerToken.Trim();
    }

    public string BearerToken { get; }
}
