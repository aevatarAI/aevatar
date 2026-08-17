using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Capabilities.Tests;

public sealed class ResponsesRouteResolverTests
{
    private static readonly ResponsesCallerScope CallerScope =
        new("scope-alpha", "owner-alpha", LlmSessionOriginKind.ApiKey);

    [Fact]
    public async Task ResolveRouteTargetAsync_ShouldPreserveCatalogIdentityFromApplicationDecision()
    {
        var application = new RecordingRouteApplicationService
        {
            Source = new NyxIdResolvedCatalogModelSource(
                "catalog service/alpha",
                "chrono-llm"),
        };
        var resolver = CreateResolver(application);

        var target = await resolver.ResolveRouteTargetAsync(
            "chrono-llm",
            "gpt-5.5",
            CallerScope,
            CancellationToken.None);

        target.Should().NotBeNull();
        target!.SourceIdentityCase.Should().Be(
            LLMRouteTarget.SourceIdentityOneofCase.CatalogServiceId);
        target.CatalogServiceId.Should().Be("catalog service/alpha");
        target.ServiceSlugSnapshot.Should().Be("chrono-llm");
        application.LastScopeId.Should().Be("scope-alpha");
        application.LastServiceSlug.Should().Be("chrono-llm");
        application.LastUpstreamModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task ResolveRouteTargetAsync_ShouldPreserveExactScopeUserServiceIdentity()
    {
        var resolver = CreateResolver(new RecordingRouteApplicationService
        {
            Source = new NyxIdResolvedUserModelSource("user-legacy", "legacy-llm"),
        });

        var target = await resolver.ResolveRouteTargetAsync(
            "legacy-llm",
            "model-a",
            CallerScope,
            CancellationToken.None);

        target.Should().NotBeNull();
        target!.SourceIdentityCase.Should().Be(
            LLMRouteTarget.SourceIdentityOneofCase.UserServiceId);
        target.UserServiceId.Should().Be("user-legacy");
        target.ServiceSlugSnapshot.Should().Be("legacy-llm");
    }

    [Fact]
    public async Task ResolveRouteTargetAsync_ShouldReturnNullWhenApplicationFindsNoSource()
    {
        var resolver = CreateResolver(new RecordingRouteApplicationService());

        var target = await resolver.ResolveRouteTargetAsync(
            "shared-runtime",
            "model-a",
            CallerScope,
            CancellationToken.None);

        target.Should().BeNull();
    }

    [Fact]
    public async Task ResolveRouteTargetAsync_ShouldBridgeApplicationFailureToResponsesFailure()
    {
        var sourceFailure = new HttpRequestException("inventory offline");
        var applicationFailure = new LLMModelCatalogApplicationException(
            LLMModelCatalogApplicationErrorKind.Unavailable,
            "MODEL_ROUTE_UNAVAILABLE",
            "Model routing is unavailable.",
            sourceFailure);
        var resolver = CreateResolver(new RecordingRouteApplicationService
        {
            Exception = applicationFailure,
        });

        var act = () => resolver.ResolveRouteTargetAsync(
            "chrono-llm",
            "model-a",
            CallerScope,
            CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ResponsesRouteUnavailableException>();
        thrown.Which.InnerException.Should().BeSameAs(sourceFailure);
    }

    [Fact]
    public async Task ResolveRouteTargetAsync_ShouldPreserveCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sourceFailure = new OperationCanceledException(cancellation.Token);
        var resolver = CreateResolver(new RecordingRouteApplicationService
        {
            Exception = sourceFailure,
        });

        var act = () => resolver.ResolveRouteTargetAsync(
            "chrono-llm",
            "model-a",
            CallerScope,
            cancellation.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().BeSameAs(sourceFailure);
    }

    private static ResponsesRouteResolver CreateResolver(
        ILLMModelRouteApplicationService application) =>
        new(application, NullLogger<ResponsesRouteResolver>.Instance);

    private sealed class RecordingRouteApplicationService : ILLMModelRouteApplicationService
    {
        public NyxIdResolvedModelSource? Source { get; init; }

        public Exception? Exception { get; init; }

        public string? LastScopeId { get; private set; }

        public string? LastServiceSlug { get; private set; }

        public string? LastUpstreamModelId { get; private set; }

        public Task<NyxIdResolvedModelSource?> ResolveAsync(
            string scopeId,
            string serviceSlug,
            string upstreamModelId,
            CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            LastServiceSlug = serviceSlug;
            LastUpstreamModelId = upstreamModelId;
            return Exception is null
                ? Task.FromResult(Source)
                : Task.FromException<NyxIdResolvedModelSource?>(Exception);
        }
    }
}
