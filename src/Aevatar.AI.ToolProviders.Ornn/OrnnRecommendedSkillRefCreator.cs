using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.CatalogSkills;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn;

public sealed class OrnnRecommendedSkillRefCreator : INyxIdRecommendedSkillRefCreator
{
    private readonly INyxIdClientCredentialsTokenSource _tokenSource;
    private readonly OrnnSkillPublishingService _publishingService;
    private readonly NyxIdRecommendedSkillRefPersistenceService _persistenceService;
    private readonly NyxIdRecommendedSkillGenerator _skillGenerator;
    private readonly OrnnSkillClient _skillClient;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<NyxIdRecommendedSkillRef>> _createdRefs = new(StringComparer.Ordinal);

    public OrnnRecommendedSkillRefCreator(
        INyxIdClientCredentialsTokenSource tokenSource,
        OrnnSkillPublishingService publishingService,
        NyxIdRecommendedSkillRefPersistenceService persistenceService,
        NyxIdRecommendedSkillGenerator skillGenerator,
        OrnnSkillClient skillClient,
        ILogger<OrnnRecommendedSkillRefCreator>? logger = null)
    {
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _publishingService = publishingService ?? throw new ArgumentNullException(nameof(publishingService));
        _persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));
        _skillGenerator = skillGenerator ?? throw new ArgumentNullException(nameof(skillGenerator));
        _skillClient = skillClient ?? throw new ArgumentNullException(nameof(skillClient));
        _logger = logger ?? NullLogger<OrnnRecommendedSkillRefCreator>.Instance;
    }

    public async Task<NyxIdRecommendedSkillRefCreationResult> CreateRecommendedSkillRefsAsync(
        NyxIdServiceInstance instance,
        string documentAccessToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var serverToken = await _tokenSource.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(serverToken))
                return NyxIdRecommendedSkillRefCreationResult.Empty();

            if (string.IsNullOrWhiteSpace(instance.CatalogServiceId))
            {
                _logger.LogWarning(
                    "NyxID recommended skill ref creation skipped because catalog_service_id is missing for user service {UserServiceId}",
                    instance.UserServiceId);
                return NyxIdRecommendedSkillRefCreationResult.Empty(
                    NyxIdRecommendedSkillRefPersistenceStatus.WriteUnavailable,
                    "catalog_service_id_missing");
            }

            var generatedSkill = await _skillGenerator
                .GenerateAsync(serverToken, instance, ct)
                .ConfigureAwait(false);
            if (generatedSkill is null)
            {
                _logger.LogWarning(
                    "NyxID recommended skill generation is unavailable for catalog slug {CatalogServiceSlug}",
                    instance.CatalogServiceSlug);
                return NyxIdRecommendedSkillRefCreationResult.Empty();
            }

            var cacheKey = BuildCacheKey(instance, generatedSkill);
            if (_createdRefs.TryGetValue(cacheKey, out var cachedRefs))
                return await PersistCreatedRefsAsync(serverToken, documentAccessToken, instance, cachedRefs, generatedSkill, ct).ConfigureAwait(false);

            var request = BuildPublishRequest(generatedSkill);
            var publishResult = await _publishingService.PublishAsync(serverToken, request, ct).ConfigureAwait(false);
            if (!publishResult.IsSuccess ||
                string.IsNullOrWhiteSpace(publishResult.Guid) ||
                string.IsNullOrWhiteSpace(publishResult.Version) ||
                string.IsNullOrWhiteSpace(publishResult.SkillHash))
            {
                _logger.LogWarning(
                    "Ornn recommended skill creation failed for catalog slug {CatalogServiceSlug} with status {Status}",
                    instance.CatalogServiceSlug,
                    publishResult.Status);
                return MapPublishFailure(publishResult);
            }

            var refs = new[]
            {
                new NyxIdRecommendedSkillRef
                {
                    Source = NyxIdRecommendedSkillSource.Ornn,
                    SkillId = publishResult.Guid.Trim(),
                    LiteralVersion = publishResult.Version.Trim(),
                    ManifestDigest = publishResult.SkillHash.Trim(),
                    DisplayName = generatedSkill.DisplayName,
                    RecommendationName = generatedSkill.RecommendationName,
                    Revision = generatedSkill.Revision,
                },
            };
            _createdRefs[cacheKey] = refs;
            return await PersistCreatedRefsAsync(serverToken, documentAccessToken, instance, refs, generatedSkill, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static NyxIdRecommendedSkillRefCreationResult MapPublishFailure(OrnnSkillPublishingResult publishResult) =>
        string.Equals(publishResult.Status, "permission_update_failed", StringComparison.Ordinal)
            ? NyxIdRecommendedSkillRefCreationResult.Empty(
                NyxIdRecommendedSkillRefPersistenceStatus.WriteDenied,
                publishResult.Failure?.Code ?? publishResult.Status)
            : NyxIdRecommendedSkillRefCreationResult.Empty();

    private async Task<NyxIdRecommendedSkillRefCreationResult> PersistCreatedRefsAsync(
        string serverToken,
        string consumerToken,
        NyxIdServiceInstance instance,
        IReadOnlyList<NyxIdRecommendedSkillRef> refs,
        GeneratedCatalogSkillContent generatedSkill,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(consumerToken))
            return VerificationFailed();
        var verifiedSkills = new List<NyxIdCreatedRecommendedSkill>(refs.Count);
        try
        {
            foreach (var skillRef in refs)
            {
                var detailRead = await _skillClient.GetExactSkillDetailAsync(
                    consumerToken, skillRef.SkillId, skillRef.LiteralVersion, ct).ConfigureAwait(false);
                if (detailRead.Value is not { } detail || detail.Guid != skillRef.SkillId ||
                    detail.Name != generatedSkill.Name || detail.Version != skillRef.LiteralVersion ||
                    detail.SkillHash != skillRef.ManifestDigest || !NyxIdCatalogSkillClient.IsDigest(detail.SkillHash) || detail.IsPrivate is not false)
                    return VerificationFailed(detailRead.ProxyStatus is 401 or 403);
                var contentRead = await _skillClient.GetExactSkillJsonAsync(
                    consumerToken, skillRef.SkillId, skillRef.LiteralVersion, ct).ConfigureAwait(false);
                if (contentRead.Value is not { } content || content.Name != detail.Name || content.Version != skillRef.LiteralVersion)
                    return VerificationFailed(contentRead.ProxyStatus is 401 or 403);
                var documents = content.Files?.Where(file => file.Key == "SKILL.md" || file.Key == $"{detail.Name}/SKILL.md").ToArray();
                if (documents is not { Length: 1 } || string.IsNullOrWhiteSpace(documents[0].Value))
                    return VerificationFailed();
                verifiedSkills.Add(new(skillRef.Clone(), detail.Name, detail.CreatedBy ?? string.Empty, documents[0].Value));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return VerificationFailed();
        }

        var persistenceResult = await _persistenceService.PersistRecommendedSkillRefsAsync(
            serverToken,
            instance,
            refs,
            ct).ConfigureAwait(false);
        if (!persistenceResult.IsSuccess)
        {
            _logger.LogWarning(
                "NyxID recommended skill ref persistence failed for user service {UserServiceId} with status {Status} and code {FailureCode}",
                instance.UserServiceId,
                persistenceResult.Status,
                persistenceResult.FailureCode);
        }

        return new NyxIdRecommendedSkillRefCreationResult(
            refs,
            verifiedSkills,
            persistenceResult.Status,
            persistenceResult.FailureCode);
    }

    private static NyxIdRecommendedSkillRefCreationResult VerificationFailed(bool denied = false) =>
        NyxIdRecommendedSkillRefCreationResult.Empty(
            denied ? NyxIdRecommendedSkillRefPersistenceStatus.ReadDenied : NyxIdRecommendedSkillRefPersistenceStatus.ReadUnavailable,
            "ornn_publication_verification_failed");

    private static OrnnSkillPublishRequest BuildPublishRequest(GeneratedCatalogSkillContent skill) =>
        new()
        {
            Name = skill.Name,
            Description = skill.Description,
            Version = "1.0",
            Category = skill.Category,
            InstructionsMarkdown = skill.InstructionsMarkdown,
            Visibility = "public",
            Tags = skill.Tags,
            ToolList = skill.ToolList,
        };

    private static string BuildCacheKey(
        NyxIdServiceInstance instance,
        GeneratedCatalogSkillContent skill) =>
        string.Join(
            '|',
            instance.UserServiceId,
            instance.CatalogServiceSlug,
            instance.DisplaySlug,
            skill.Name,
            "1.0",
            skill.Revision);

}
