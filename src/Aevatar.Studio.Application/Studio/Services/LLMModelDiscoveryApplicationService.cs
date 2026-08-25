using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed class LLMModelDiscoveryApplicationService : ILLMModelDiscoveryApplicationService
{
    private readonly LLMModelSourceResolver _sourceResolver;

    public LLMModelDiscoveryApplicationService(LLMModelSourceResolver sourceResolver)
    {
        _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
    }

    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        IReadOnlyList<ResolvedLLMModelSource> sources;
        try
        {
            sources = await _sourceResolver
                .ResolveAsync(scopeId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Unavailable(
                "The model catalog policy is temporarily unavailable.",
                ex);
        }

        return sources
            .SelectMany(static source => BuildDescriptors(source))
            .OrderBy(static model => model.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<LLMModelDescriptor> BuildDescriptors(ResolvedLLMModelSource source) =>
        source.ModelSelection.UpstreamModelIds.Select(modelId => new LLMModelDescriptor(
            $"{source.Source.ServiceSlug}/{modelId}",
            Created: 0,
            OwnedBy: source.Source.ServiceSlug,
            Group: source.Source.ServiceSlug,
            ContextLength: null,
            MaxOutputTokens: null,
            DisplayName: null,
            Description: null));

    private static LLMModelCatalogApplicationException Unavailable(
        string message,
        Exception? innerException = null) =>
        new(
            LLMModelCatalogApplicationErrorKind.Unavailable,
            "MODEL_CATALOG_UNAVAILABLE",
            message,
            innerException);
}
