using Aevatar.AI.ToolProviders.NyxId.CatalogSkills;
using Aevatar.GAgentService.Abstractions.CatalogSkills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>Shared authorized catalog reading and pure public-content generation.</summary>
public sealed class NyxIdRecommendedSkillGenerator
{
    public const string FixedInvokeToolName = CatalogSkillContentRenderer.FixedInvokeToolName;
    public const string DocumentRequestInstructionMarker = CatalogSkillContentRenderer.DocumentRequestInstructionMarker;
    private readonly NyxIdCatalogSkillContentReader _reader;
    private readonly ILogger _logger;
    private readonly CatalogSkillContentRenderer _renderer = new();

    public NyxIdRecommendedSkillGenerator(NyxIdApiClient client, ILogger<NyxIdRecommendedSkillGenerator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _logger = logger ?? NullLogger<NyxIdRecommendedSkillGenerator>.Instance;
        _reader = new NyxIdCatalogSkillContentReader(new NyxIdCatalogSkillClient(client), _logger);
    }

    public async Task<GeneratedCatalogSkillContent> GenerateAsync(string token, string catalogServiceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogServiceId);
        var input = await _reader.ReadAsync(token, catalogServiceId, ct).ConfigureAwait(false);
        return _renderer.Render(input);
    }

    public async Task<GeneratedCatalogSkillContent?> GenerateAsync(string token, NyxIdServiceInstance instance, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrWhiteSpace(instance.CatalogServiceId))
        {
            _logger.LogWarning("Catalog skill generation failed. stage=resolve_target code=catalog_service_id_missing");
            return null;
        }
        try
        {
            return await GenerateAsync(token, instance.CatalogServiceId, ct).ConfigureAwait(false);
        }
        catch (CatalogRecommendedSkillUpdateException exception)
        {
            _logger.LogWarning("Catalog skill generation failed. catalogServiceId={CatalogServiceId} stage={Stage} code={Code} httpStatus={HttpStatus}",
                instance.CatalogServiceId, exception.Stage, exception.Code, exception.DownstreamStatusCode);
            return null;
        }
    }
}
