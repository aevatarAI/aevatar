namespace Aevatar.GAgentService.Abstractions.CatalogSkills;

/// <summary>
/// External catalog/skill boundary. Publisher credentials authorize catalog reads,
/// package generation, publication, visibility changes and catalog writes; the
/// request credential is used only for the post-publication consumer read.
/// </summary>
public interface ICatalogRecommendedSkillUpdateSteps
{
    Task<CatalogRecommendedSkillUpdateTarget> ResolveTargetAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CancellationToken ct = default);

    Task<CatalogRecommendedSkillUpdatePackage> PreparePackageAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillUpdateTarget target,
        CancellationToken ct = default);

    Task<CatalogRecommendedSkillPublication> PublishVersionAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillUpdatePackage package,
        CancellationToken ct = default);

    Task<CatalogRecommendedSkillPublication> VerifyPublicationAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillPublication publication,
        CatalogRecommendedSkillUpdateExecutionCredential credential,
        CancellationToken ct = default);

    Task<CatalogRecommendedSkillReferenceWrite> PersistReferenceAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillUpdateTarget target,
        CatalogRecommendedSkillPublication publication,
        CancellationToken ct = default);

    Task<CatalogRecommendedSkillReferenceWrite> VerifyReferenceAsync(
        CatalogRecommendedSkillUpdateRequest request,
        CatalogRecommendedSkillPublication publication,
        CatalogRecommendedSkillReferenceWrite reference,
        CancellationToken ct = default);
}

/// <summary>A generated public archive used within one publication attempt; never a credential container.</summary>
public sealed record CatalogRecommendedSkillUpdatePackage(byte[] ZipBytes, string SkillName);

public sealed class CatalogRecommendedSkillUpdateException : Exception
{
    public CatalogRecommendedSkillUpdateException(
        CatalogRecommendedSkillUpdateErrorCode code,
        CatalogRecommendedSkillUpdateStage stage,
        string safeMessage,
        int? downstreamStatusCode = null,
        bool outcomeUncertain = false) : base(safeMessage)
    {
        Code = code;
        Stage = stage;
        DownstreamStatusCode = downstreamStatusCode;
        OutcomeUncertain = outcomeUncertain;
    }

    public CatalogRecommendedSkillUpdateErrorCode Code { get; }
    public CatalogRecommendedSkillUpdateStage Stage { get; }
    public int? DownstreamStatusCode { get; }
    public bool OutcomeUncertain { get; }
}
