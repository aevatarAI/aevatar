using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed class LLMModelRouteApplicationService : ILLMModelRouteApplicationService
{
    private readonly LLMModelSourceResolver _sourceResolver;

    public LLMModelRouteApplicationService(LLMModelSourceResolver sourceResolver)
    {
        _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
    }

    public async Task<NyxIdResolvedModelSource?> ResolveAsync(
        string scopeId,
        string serviceSlug,
        string upstreamModelId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamModelId);

        try
        {
            var sources = await _sourceResolver
                .ResolveAsync(scopeId, ct)
                .ConfigureAwait(false);
            return sources
                .SingleOrDefault(source => string.Equals(
                    source.Source.ServiceSlug,
                    serviceSlug.Trim(),
                    StringComparison.Ordinal) &&
                    source.ModelSelection.UpstreamModelIds.Contains(upstreamModelId, StringComparer.Ordinal))
                ?.Source;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LLMModelCatalogApplicationException(
                LLMModelCatalogApplicationErrorKind.Unavailable,
                "MODEL_ROUTE_UNAVAILABLE",
                "The model routing policy is temporarily unavailable.",
                ex);
        }
    }
}
