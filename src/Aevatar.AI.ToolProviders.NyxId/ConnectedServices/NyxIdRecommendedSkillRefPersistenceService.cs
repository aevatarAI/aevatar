using Aevatar.AI.ToolProviders.NyxId.CatalogSkills;
using Aevatar.GAgentService.Abstractions.CatalogSkills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public enum NyxIdRecommendedSkillRefPersistenceStatus
{
    Succeeded,
    EmptyInput,
    ReadDenied,
    ReadUnavailable,
    WriteDenied,
    WriteUnavailable,
}

public sealed record NyxIdRecommendedSkillRefPersistenceResult(
    NyxIdRecommendedSkillRefPersistenceStatus Status,
    IReadOnlyList<NyxIdRecommendedSkillRef> Refs,
    string FailureCode)
{
    public bool IsSuccess => Status is NyxIdRecommendedSkillRefPersistenceStatus.Succeeded;

    public static NyxIdRecommendedSkillRefPersistenceResult Succeeded(
        IReadOnlyList<NyxIdRecommendedSkillRef> refs) =>
        new(NyxIdRecommendedSkillRefPersistenceStatus.Succeeded, refs, string.Empty);

    public static NyxIdRecommendedSkillRefPersistenceResult EmptyInput() =>
        new(NyxIdRecommendedSkillRefPersistenceStatus.EmptyInput, [], string.Empty);

    public static NyxIdRecommendedSkillRefPersistenceResult Failed(
        NyxIdRecommendedSkillRefPersistenceStatus status,
        string failureCode) =>
        new(status, [], failureCode);
}

public sealed class NyxIdRecommendedSkillRefPersistenceService
{
    private readonly NyxIdCatalogSkillClient _catalog;
    private readonly ILogger<NyxIdRecommendedSkillRefPersistenceService> _logger;

    public NyxIdRecommendedSkillRefPersistenceService(
        NyxIdApiClient client,
        ILogger<NyxIdRecommendedSkillRefPersistenceService>? logger = null)
    {
        _catalog = new NyxIdCatalogSkillClient(client ?? throw new ArgumentNullException(nameof(client)));
        _logger = logger ?? NullLogger<NyxIdRecommendedSkillRefPersistenceService>.Instance;
    }

    public async Task<NyxIdRecommendedSkillRefPersistenceResult> PersistRecommendedSkillRefsAsync(
        string serverToken,
        NyxIdServiceInstance instance,
        IReadOnlyList<NyxIdRecommendedSkillRef> refs,
        CancellationToken ct)
    {
        if (refs.Count == 0)
            return NyxIdRecommendedSkillRefPersistenceResult.EmptyInput();
        if (string.IsNullOrWhiteSpace(instance.CatalogServiceId))
            return Failure(NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable, "catalog_service_id_missing");

        var writing = false;
        try
        {
            var current = await _catalog.ReadRecommendationsAsync(serverToken, instance.CatalogServiceId, ct).ConfigureAwait(false);
            var desired = current.Clone();
            foreach (var created in refs)
            {
                var replacement = Map(created);
                var existing = desired.Recommendations.FirstOrDefault(item => item.Source == replacement.Source && item.SkillId == replacement.SkillId);
                // Creation must not silently retarget an existing recommendation name.
                if (existing is null && desired.Recommendations.Any(item => item.Name == replacement.Name))
                    return Failure(NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable, "recommendation_identity_conflict");
                if (existing is not null)
                {
                    // A pending creation cannot undo a later administrator update, even with a fresh CAS revision.
                    if (existing.Name != replacement.Name || existing.Version != replacement.Version || existing.ManifestDigest != replacement.ManifestDigest)
                        return Failure(NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable, "recommendation_version_conflict");
                }
                else
                    desired.Recommendations.Add(replacement);
            }
            if (current.Recommendations.SequenceEqual(desired.Recommendations))
                return NyxIdRecommendedSkillRefPersistenceResult.Succeeded(refs);

            writing = true;
            var written = await _catalog.ReplaceRecommendationsAsync(serverToken, instance.CatalogServiceId,
                current.CatalogSkillsRevision, Guid.NewGuid().ToString("D"), desired.Recommendations, ct).ConfigureAwait(false);
            var observed = await _catalog.ReadRecommendationsAsync(serverToken, instance.CatalogServiceId, ct).ConfigureAwait(false);
            if (written.CatalogSkillsRevision <= current.CatalogSkillsRevision ||
                observed.CatalogSkillsRevision != written.CatalogSkillsRevision ||
                !written.Recommendations.SequenceEqual(desired.Recommendations) ||
                !observed.Recommendations.SequenceEqual(desired.Recommendations))
                return Failure(NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable, "catalog_reference_verification_failed");
            return NyxIdRecommendedSkillRefPersistenceResult.Succeeded(refs);
        }
        catch (CatalogRecommendedSkillUpdateException error)
        {
            var denied = error.Code == CatalogRecommendedSkillUpdateErrorCode.Forbidden;
            var status = (writing, denied) switch
            {
                (false, true) => NyxIdRecommendedSkillRefPersistenceStatus.ReadDenied,
                (false, false) => NyxIdRecommendedSkillRefPersistenceStatus.ReadUnavailable,
                (true, true) => NyxIdRecommendedSkillRefPersistenceStatus.WriteDenied,
                _ => NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable,
            };
            _logger.LogWarning("Catalog recommendation persistence failed. catalogServiceId={CatalogServiceId} code={Code} status={HttpStatus}",
                instance.CatalogServiceId, error.Code, error.DownstreamStatusCode);
            return Failure(status, $"catalog_recommended_skill_refs_{error.Code}");
        }
        catch (HttpRequestException)
        {
            return Failure(writing ? NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable : NyxIdRecommendedSkillRefPersistenceStatus.ReadUnavailable,
                "catalog_request_unavailable");
        }
    }

    private static CatalogRecommendedSkillReference Map(NyxIdRecommendedSkillRef reference) => new()
    {
        Source = reference.Source == NyxIdRecommendedSkillSource.Ornn ? "ornn" : string.Empty,
        SkillId = reference.SkillId,
        Name = string.IsNullOrWhiteSpace(reference.RecommendationName) ? reference.DisplayName : reference.RecommendationName,
        Version = reference.LiteralVersion,
        ManifestDigest = reference.ManifestDigest,
    };

    private static NyxIdRecommendedSkillRefPersistenceResult Failure(NyxIdRecommendedSkillRefPersistenceStatus status, string code) =>
        NyxIdRecommendedSkillRefPersistenceResult.Failed(status, code);
}
