using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Capabilities.Tests;

public sealed class ResponsesRouteResolverTests
{
    [Fact]
    public async Task ResolveRouteValueAsync_ShouldReturnSlugRouteValue_FromCatalog()
    {
        var catalog = new RecordingCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("anthropic", "/api/v1/llm/anthropic/v1", allowed: true),
            MakeService("chrono-llm", "/api/v1/proxy/s/chrono-llm", allowed: true),
        ], null));
        var resolver = new ResponsesRouteResolver(catalog, NullLogger<ResponsesRouteResolver>.Instance);

        (await resolver.ResolveRouteValueAsync("anthropic", "bearer-1", CancellationToken.None))
            .Should().Be("/api/v1/llm/anthropic/v1");
        (await resolver.ResolveRouteValueAsync("chrono-llm", "bearer-1", CancellationToken.None))
            .Should().Be("/api/v1/proxy/s/chrono-llm");
    }

    [Fact]
    public async Task ResolveRouteValueAsync_ShouldReturnNullForUnknownSlug()
    {
        var catalog = new RecordingCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("anthropic", "/api/v1/llm/anthropic/v1", allowed: true),
        ], null));
        var resolver = new ResponsesRouteResolver(catalog, NullLogger<ResponsesRouteResolver>.Instance);

        (await resolver.ResolveRouteValueAsync("mistralai", "bearer-1", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task ResolveRouteValueAsync_ShouldIncludeDisallowedServicesSoDownstreamCanReturnHonestError()
    {
        // Catalog's `Allowed=false` reflects "user hasn't bound a credential" (a UI hint),
        // not "this route can't serve requests." Several services with
        // `requires_connection=true + connected=false` (e.g. chrono-llm in prod) still
        // serve traffic because the backing LLM is shared at deployment level. Including
        // the route lets NyxID return the honest 403/404 if the caller really can't reach
        // the upstream, instead of aevatar pretending the slug doesn't exist.
        var catalog = new RecordingCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("chrono-llm", "/api/v1/proxy/s/chrono-llm", allowed: false),
        ], null));
        var resolver = new ResponsesRouteResolver(catalog, NullLogger<ResponsesRouteResolver>.Instance);

        (await resolver.ResolveRouteValueAsync("chrono-llm", "bearer-1", CancellationToken.None))
            .Should().Be("/api/v1/proxy/s/chrono-llm");
    }

    [Fact]
    public async Task ResolveRouteValueAsync_ShouldUseCatalogPortResultWithoutOwningCache()
    {
        var catalog = new MutableCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("anthropic", "/api/v1/llm/anthropic/v1", allowed: true),
        ], null));
        var resolver = new ResponsesRouteResolver(catalog, NullLogger<ResponsesRouteResolver>.Instance);

        (await resolver.ResolveRouteValueAsync("anthropic", "bearer-1", CancellationToken.None))
            .Should().Be("/api/v1/llm/anthropic/v1");

        catalog.Result = new NyxIdLlmServicesResult(
        [
            MakeService("anthropic", "/api/v1/llm/anthropic/v2", allowed: true),
        ], null);

        (await resolver.ResolveRouteValueAsync("anthropic", "bearer-1", CancellationToken.None))
            .Should().Be("/api/v1/llm/anthropic/v2");
        catalog.FetchCount.Should().Be(2, "the resolver delegates catalog freshness to IUserLlmCatalogPort");
    }

    [Fact]
    public async Task ResolveRouteValueAsync_ShouldReadCatalogForEachBearer()
    {
        var catalog = new RecordingCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("anthropic", "/api/v1/llm/anthropic/v1", allowed: true),
        ], null));
        var resolver = new ResponsesRouteResolver(catalog, NullLogger<ResponsesRouteResolver>.Instance);

        await resolver.ResolveRouteValueAsync("anthropic", "bearer-A", CancellationToken.None);
        await resolver.ResolveRouteValueAsync("anthropic", "bearer-B", CancellationToken.None);

        catalog.FetchCount.Should().Be(2, "caller/authority cache boundaries are owned by IUserLlmCatalogPort");
    }

    private static NyxIdLlmService MakeService(string slug, string routeValue, bool allowed) =>
        new(
            CatalogEntryId: slug,
            ServiceSlug: slug,
            DisplayName: slug,
            RouteValue: routeValue,
            ModelCatalog: LLMSelectionPolicy.NormalizeCatalog(
                [],
                null,
                LLMModelCatalogDiagnosticKind.NotPublished),
            Status: allowed ? "ready" : "not_connected",
            Source: NyxIdLlmProviderSource.GatewayProvider,
            Allowed: allowed,
            Description: null);

    private sealed class RecordingCatalogPort : IUserLlmCatalogPort
    {
        private readonly NyxIdLlmServicesResult _result;
        public int FetchCount { get; private set; }

        public RecordingCatalogPort(NyxIdLlmServicesResult result) => _result = result;

        public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct)
        {
            FetchCount++;
            return Task.FromResult(_result);
        }

        public Task<NyxIdLlmServicesResult> GetFreshServicesAsync(string bearerToken, CancellationToken ct) =>
            GetServicesAsync(bearerToken, ct);

        public Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct) =>
            throw new NotSupportedException("Provision not used by route resolver.");
    }

    private sealed class MutableCatalogPort : IUserLlmCatalogPort
    {
        public int FetchCount { get; private set; }
        public NyxIdLlmServicesResult Result { get; set; }

        public MutableCatalogPort(NyxIdLlmServicesResult result) => Result = result;

        public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct)
        {
            FetchCount++;
            return Task.FromResult(Result);
        }

        public Task<NyxIdLlmServicesResult> GetFreshServicesAsync(string bearerToken, CancellationToken ct) =>
            GetServicesAsync(bearerToken, ct);

        public Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct) =>
            throw new NotSupportedException("Provision not used by route resolver.");
    }
}
