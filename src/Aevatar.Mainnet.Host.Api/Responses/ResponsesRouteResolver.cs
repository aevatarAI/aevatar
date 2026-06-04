using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>Resolves an OpenRouter-style vendor prefix (e.g. `anthropic`) into the full NyxID
/// route path (e.g. `/api/v1/llm/anthropic/v1`) by consulting the user's LLM service catalog.
/// Used by `/v1/responses` to convert a `vendor/model` request into a `NyxIdRoutePreference`
/// that <see cref="Aevatar.AI.LLMProviders.NyxId.NyxIdLLMProvider"/> can route. Returns
/// <c>null</c> when the slug isn't a known service — caller falls back to default gateway
/// routing (treats the whole string as a bare model name).</summary>
internal sealed class ResponsesRouteResolver : IResponsesRouteResolver
{
    // Refactor (iter26/cluster-026-responses-route-user-catalog-cache):
    //   Old pattern: Responses/Messages routes resolve `vendor/model` by reading a singleton per-bearer in-process cache of NyxID user LLM service catalog facts.
    //   New principle: Resolve model route from the current catalog read in the request flow; do not store user route facts in singleton process memory.
    private readonly IUserLlmCatalogPort _catalog;
    private readonly ILogger<ResponsesRouteResolver> _logger;

    public ResponsesRouteResolver(
        IUserLlmCatalogPort catalog,
        ILogger<ResponsesRouteResolver> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> ResolveRouteValueAsync(
        string slug,
        string bearerToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var normalizedSlug = slug.Trim();
        var routes = await ReadCurrentRoutesAsync(bearerToken, ct).ConfigureAwait(false);
        return routes.TryGetValue(normalizedSlug, out var routeValue) ? routeValue : null;
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadCurrentRoutesAsync(
        string bearerToken,
        CancellationToken ct)
    {
        NyxIdLlmServicesResult result;
        try
        {
            result = await _catalog.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog fetch failed; route resolution will treat all slugs as unknown.");
            return EmptyRoutes;
        }

        return BuildRouteMap(result);
    }

    private static Dictionary<string, string> BuildRouteMap(NyxIdLlmServicesResult result)
    {
        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in result.Services)
        {
            // Note: we do NOT gate on `service.Allowed`. Catalog `Allowed=false` flags
            // "user hasn't bound a credential" (a UI hint), not "this route can't serve
            // requests" — several services with `requires_connection=true` are still
            // routable for the caller's bearer if the backing LLM is shared. Map every
            // service with a slug+route; downstream HTTP error tells the truth if the
            // caller actually can't reach the upstream.
            if (string.IsNullOrWhiteSpace(service.ServiceSlug)) continue;
            if (string.IsNullOrWhiteSpace(service.RouteValue)) continue;
            // First-write-wins. Different service shapes (GatewayProvider via /llm/<slug>/v1
            // vs ProxyService via /proxy/s/<slug>) should never share a slug — but if they
            // somehow do, prefer the entry the catalog returned first.
            routes.TryAdd(service.ServiceSlug.Trim(), service.RouteValue.Trim());
        }
        return routes;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyRoutes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Application facades directly depended on ChatRouting.Core query/resolver implementations to decide the ingress route.
//   New principle: Host composes the Application route-decision port with the concrete readmodel query and resolver; Application owns only the command semantics.
internal sealed class ResponsesChatRouteDecisionPort(
    IChatRoutePolicyQueryPort queryPort,
    ChatRouteResolver resolver) : IResponsesChatRouteDecisionPort
{
    public async Task<ChatRouteDecision> ResolveAsync(
        ResponsesChatRouteDecisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ownerScope = OwnerScope.ForNyxIdNative(request.CallerScope.ScopeId);
        var snapshot = await queryPort.LookupForCallerAsync(ownerScope, ct);
        return resolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.NyxResponses,
            CallerScope = ownerScope.Clone(),
            Channel = string.Empty,
            CommandName = request.CommandName,
            ContentHint = request.ContentHint,
            ToolMode = request.ToolMode,
            Model = request.Model,
        });
    }
}
