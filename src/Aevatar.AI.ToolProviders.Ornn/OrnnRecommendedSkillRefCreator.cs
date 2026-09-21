using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Ornn;

public sealed class OrnnRecommendedSkillRefCreator : INyxIdRecommendedSkillRefCreator
{
    private readonly NyxIdToolOptions _options;
    private readonly INyxIdClientCredentialsTokenSource _tokenSource;
    private readonly OrnnSkillPublishingService _publishingService;
    private readonly NyxIdRecommendedSkillRefPersistenceService _persistenceService;
    private readonly NyxIdRecommendedSkillGenerator _skillGenerator;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<NyxIdRecommendedSkillRef>> _createdRefs = new(StringComparer.Ordinal);

    public OrnnRecommendedSkillRefCreator(
        NyxIdToolOptions options,
        INyxIdClientCredentialsTokenSource tokenSource,
        OrnnSkillPublishingService publishingService,
        NyxIdRecommendedSkillRefPersistenceService persistenceService,
        NyxIdRecommendedSkillGenerator skillGenerator,
        ILogger<OrnnRecommendedSkillRefCreator>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _publishingService = publishingService ?? throw new ArgumentNullException(nameof(publishingService));
        _persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));
        _skillGenerator = skillGenerator ?? throw new ArgumentNullException(nameof(skillGenerator));
        _logger = logger ?? NullLogger<OrnnRecommendedSkillRefCreator>.Instance;
    }

    public async Task<IReadOnlyList<NyxIdRecommendedSkillRef>> CreateRecommendedSkillRefsAsync(
        NyxIdServiceInstance instance,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var template = ResolveTemplate(instance);
        if (template is null)
            return [];

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var token = await _tokenSource.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return [];

            var generatedSkill = await _skillGenerator
                .GenerateAsync(token, instance, template, ct)
                .ConfigureAwait(false);
            if (generatedSkill is null)
            {
                _logger.LogWarning(
                    "NyxID recommended skill generation found no operation contracts for catalog slug {CatalogServiceSlug}",
                    instance.CatalogServiceSlug);
                return [];
            }

            var cacheKey = BuildCacheKey(instance, generatedSkill);
            if (_createdRefs.TryGetValue(cacheKey, out var cachedRefs))
                return cachedRefs;

            var request = BuildPublishRequest(generatedSkill);
            var result = await _publishingService.PublishAsync(token, request, ct).ConfigureAwait(false);
            if (!result.IsSuccess ||
                string.IsNullOrWhiteSpace(result.Guid) ||
                string.IsNullOrWhiteSpace(result.Version) ||
                string.IsNullOrWhiteSpace(result.SkillHash))
            {
                _logger.LogWarning(
                    "Ornn recommended skill creation failed for catalog slug {CatalogServiceSlug} with status {Status}",
                    instance.CatalogServiceSlug,
                    result.Status);
                return [];
            }

            var refs = new[]
            {
                new NyxIdRecommendedSkillRef
                {
                    Source = NyxIdRecommendedSkillSource.Ornn,
                    SkillId = result.Guid.Trim(),
                    LiteralVersion = result.Version.Trim(),
                    ManifestDigest = result.SkillHash.Trim(),
                    DisplayName = generatedSkill.DisplayName,
                    RecommendationName = generatedSkill.RecommendationName,
                    Revision = generatedSkill.Revision,
                },
            };
            var persistenceResult = await _persistenceService.PersistRecommendedSkillRefsAsync(
                token,
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
                return [];
            }

            _createdRefs[cacheKey] = persistenceResult.Refs;
            return persistenceResult.Refs;
        }
        finally
        {
            _gate.Release();
        }
    }

    private NyxIdRecommendedSkillCreationTemplate? ResolveTemplate(NyxIdServiceInstance instance) =>
        _options.RecommendedSkillCreationTemplates.FirstOrDefault(template => Matches(template, instance));

    private static bool Matches(
        NyxIdRecommendedSkillCreationTemplate template,
        NyxIdServiceInstance instance) =>
        (!string.IsNullOrWhiteSpace(template.CatalogServiceSlug) &&
         string.Equals(template.CatalogServiceSlug.Trim(), instance.CatalogServiceSlug, StringComparison.Ordinal)) ||
        (!string.IsNullOrWhiteSpace(template.ServiceSlug) &&
         string.Equals(template.ServiceSlug.Trim(), instance.DisplaySlug, StringComparison.Ordinal));

    private static OrnnSkillPublishRequest BuildPublishRequest(NyxIdGeneratedRecommendedSkill skill) =>
        new()
        {
            Name = skill.Name,
            Description = skill.Description,
            Version = skill.Version,
            Category = skill.Category,
            InstructionsMarkdown = skill.InstructionsMarkdown,
            Visibility = "private",
            Tags = skill.Tags,
            ToolList = skill.ToolList,
        };

    private static string BuildCacheKey(
        NyxIdServiceInstance instance,
        NyxIdGeneratedRecommendedSkill skill) =>
        string.Join(
            '|',
            instance.UserServiceId,
            instance.CatalogServiceSlug,
            instance.DisplaySlug,
            skill.Name,
            skill.Version,
            skill.Revision);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
