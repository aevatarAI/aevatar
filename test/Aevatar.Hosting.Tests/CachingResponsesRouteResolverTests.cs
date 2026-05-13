using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Hosting.Tests;

public sealed class CachingResponsesRouteResolverTests
{
    [Fact]
    public async Task ResolveRouteValueAsync_ShouldReturnSlugRouteValue_FromCatalog()
    {
        var catalog = new RecordingCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("anthropic", "/api/v1/llm/anthropic/v1", allowed: true),
            MakeService("chrono-llm", "/api/v1/proxy/s/chrono-llm", allowed: true),
        ], null));
        var resolver = new CachingResponsesRouteResolver(catalog, NullLogger<CachingResponsesRouteResolver>.Instance);

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
        var resolver = new CachingResponsesRouteResolver(catalog, NullLogger<CachingResponsesRouteResolver>.Instance);

        (await resolver.ResolveRouteValueAsync("mistralai", "bearer-1", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task ResolveRouteValueAsync_ShouldOmitDisallowedServicesFromRouteMap()
    {
        // `Allowed=false` (e.g. provider exists but caller isn't connected) means the
        // proxy will reject anyway — don't pretend the route is usable.
        var catalog = new RecordingCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("openai", "/api/v1/llm/openai/v1", allowed: false),
        ], null));
        var resolver = new CachingResponsesRouteResolver(catalog, NullLogger<CachingResponsesRouteResolver>.Instance);

        (await resolver.ResolveRouteValueAsync("openai", "bearer-1", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task ResolveRouteValueAsync_ShouldHitCacheWithinTtlAndRefetchAfter()
    {
        // Catalog HTTP fetches are expensive (~2 NyxID round-trips); the resolver
        // sits on the /v1/responses hot path. Cache must keep repeated lookups
        // for the same bearer in-memory, and naturally refresh after the TTL.
        var catalog = new RecordingCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("anthropic", "/api/v1/llm/anthropic/v1", allowed: true),
        ], null));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var resolver = new CachingResponsesRouteResolver(
            catalog,
            NullLogger<CachingResponsesRouteResolver>.Instance,
            time);

        await resolver.ResolveRouteValueAsync("anthropic", "bearer-1", CancellationToken.None);
        await resolver.ResolveRouteValueAsync("anthropic", "bearer-1", CancellationToken.None);
        catalog.FetchCount.Should().Be(1, "second lookup within TTL is a cache hit");

        time.Advance(TimeSpan.FromMinutes(6));
        await resolver.ResolveRouteValueAsync("anthropic", "bearer-1", CancellationToken.None);
        catalog.FetchCount.Should().Be(2, "TTL has passed → refetch");
    }

    [Fact]
    public async Task ResolveRouteValueAsync_ShouldKeepPerBearerCacheSeparate()
    {
        // Different bearers can see different services (per-user proxy connections).
        // Cache must key by bearer hash so user A's view doesn't leak to user B.
        var catalog = new RecordingCatalogPort(new NyxIdLlmServicesResult(
        [
            MakeService("anthropic", "/api/v1/llm/anthropic/v1", allowed: true),
        ], null));
        var resolver = new CachingResponsesRouteResolver(catalog, NullLogger<CachingResponsesRouteResolver>.Instance);

        await resolver.ResolveRouteValueAsync("anthropic", "bearer-A", CancellationToken.None);
        await resolver.ResolveRouteValueAsync("anthropic", "bearer-B", CancellationToken.None);

        catalog.FetchCount.Should().Be(2, "distinct bearers must each warm their own cache slot");
    }

    private static NyxIdLlmService MakeService(string slug, string routeValue, bool allowed) =>
        new(
            UserServiceId: slug,
            ServiceSlug: slug,
            DisplayName: slug,
            RouteValue: routeValue,
            DefaultModel: null,
            Models: [],
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

        public Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct) =>
            throw new NotSupportedException("Provision not used by route resolver.");
    }
}
