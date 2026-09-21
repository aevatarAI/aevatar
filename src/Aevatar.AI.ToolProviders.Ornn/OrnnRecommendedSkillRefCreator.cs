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
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<NyxIdRecommendedSkillRef>> _createdRefs = new(StringComparer.Ordinal);

    public OrnnRecommendedSkillRefCreator(
        NyxIdToolOptions options,
        INyxIdClientCredentialsTokenSource tokenSource,
        OrnnSkillPublishingService publishingService,
        NyxIdRecommendedSkillRefPersistenceService persistenceService,
        ILogger<OrnnRecommendedSkillRefCreator>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _publishingService = publishingService ?? throw new ArgumentNullException(nameof(publishingService));
        _persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));
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

        var cacheKey = BuildCacheKey(instance, template);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_createdRefs.TryGetValue(cacheKey, out var cachedRefs))
                return cachedRefs;

            var token = await _tokenSource.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return [];

            var request = BuildPublishRequest(template);
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
                    DisplayName = FirstNonEmpty(template.DisplayName, request.Name),
                    RecommendationName = FirstNonEmpty(template.RecommendationName, request.Name),
                    Revision = template.Revision.Trim(),
                },
            };
            var persistedRefs = await _persistenceService.PersistRecommendedSkillRefsAsync(
                token,
                instance,
                refs,
                ct).ConfigureAwait(false);
            if (persistedRefs.Count == 0)
            {
                _logger.LogWarning(
                    "NyxID recommended skill ref persistence failed for user service {UserServiceId}",
                    instance.UserServiceId);
                return [];
            }

            _createdRefs[cacheKey] = persistedRefs;
            return persistedRefs;
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

    private static OrnnSkillPublishRequest BuildPublishRequest(NyxIdRecommendedSkillCreationTemplate template) =>
        new()
        {
            Name = template.SkillName.Trim(),
            Description = template.Description.Trim(),
            Version = FirstNonEmpty(template.Version, "1.0"),
            Category = FirstNonEmpty(template.Category, "tool-based"),
            InstructionsMarkdown = template.InstructionsMarkdown.Trim(),
            Visibility = "private",
            Tags = template.Tags.Select(static tag => tag.Trim()).Where(static tag => tag.Length > 0).ToArray(),
            ToolList = template.ToolList.Select(static tool => tool.Trim()).Where(static tool => tool.Length > 0).ToArray(),
        };

    private static string BuildCacheKey(
        NyxIdServiceInstance instance,
        NyxIdRecommendedSkillCreationTemplate template) =>
        string.Join(
            '|',
            instance.CatalogServiceSlug,
            instance.DisplaySlug,
            template.SkillName.Trim(),
            template.Version.Trim());

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
