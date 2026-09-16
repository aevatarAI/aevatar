using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class LLMModelRouteApplicationServiceTests
{
    [Fact]
    public async Task ResolveAsync_ShouldReturnTypedExactSourceForUniqueSlug()
    {
        var service = CreateService(
            new StubPolicyQueryPort([UserSource("user-alpha", "chrono-runtime")]));

        var source = await service.ResolveAsync(
            "scope-alpha",
            "chrono-runtime",
            "model-a",
            CancellationToken.None);

        source.Should().Be(new NyxIdResolvedUserModelSource(
            "user-alpha",
            "chrono-runtime"));
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNullWhenSlugIsNotConfigured()
    {
        var service = CreateService(
            new StubPolicyQueryPort([UserSource("user-alpha", "alpha")]));

        var source = await service.ResolveAsync(
            "scope-alpha",
            "beta",
            "model-a",
            CancellationToken.None);

        source.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNullWhenModelIsNotAllowlistedForSource()
    {
        var service = CreateService(
            new StubPolicyQueryPort([UserSource("user-alpha", "chrono-runtime")]));

        var source = await service.ResolveAsync(
            "scope-alpha",
            "chrono-runtime",
            "model-b",
            CancellationToken.None);

        source.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldMapDependencyFailureToTypedUnavailableError()
    {
        var sourceFailure = new HttpRequestException("policy offline");
        var service = CreateService(new FailingPolicyQueryPort(sourceFailure));

        var act = () => service.ResolveAsync(
            "scope-alpha",
            "alpha",
            "model-a",
            CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<LLMModelCatalogApplicationException>();
        thrown.Which.Kind.Should().Be(LLMModelCatalogApplicationErrorKind.Unavailable);
        thrown.Which.Code.Should().Be("MODEL_ROUTE_UNAVAILABLE");
        thrown.Which.InnerException.Should().BeSameAs(sourceFailure);
    }

    [Fact]
    public async Task ResolveAsync_ShouldPreserveCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sourceFailure = new OperationCanceledException(cancellation.Token);
        var service = CreateService(new FailingPolicyQueryPort(sourceFailure));

        var act = () => service.ResolveAsync(
            "scope-alpha",
            "alpha",
            "model-a",
            cancellation.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().BeSameAs(sourceFailure);
    }

    private static LLMModelRouteApplicationService CreateService(
        ILLMModelCatalogPolicyQueryPort policy) =>
        new(new LLMModelSourceResolver(policy));

    private static LLMModelCatalogPolicySource UserSource(
        string userServiceId,
        string serviceSlug) =>
        new(
            new NyxIDUserServiceModelSourceIdentity(userServiceId),
            serviceSlug,
            new ExplicitLLMModels(["model-a"]));

    private sealed class StubPolicyQueryPort(IReadOnlyList<LLMModelCatalogPolicySource> sources)
        : ILLMModelCatalogPolicyQueryPort
    {
        public Task<LLMModelCatalogPolicySnapshot?> GetAsync(
            LLMModelCatalogPolicyOwner owner,
            CancellationToken ct = default) =>
            Task.FromResult<LLMModelCatalogPolicySnapshot?>(
                owner.Kind == LLMModelCatalogPolicyOwnerKind.Scope
                    ? new LLMModelCatalogPolicySnapshot(
                        owner,
                        LLMModelCatalogPolicyMode.Custom,
                        sources,
                        1,
                        DateTimeOffset.UnixEpoch)
                    : null);
    }

    private sealed class FailingPolicyQueryPort(Exception exception) : ILLMModelCatalogPolicyQueryPort
    {
        public Task<LLMModelCatalogPolicySnapshot?> GetAsync(
            LLMModelCatalogPolicyOwner owner,
            CancellationToken ct = default) =>
            Task.FromException<LLMModelCatalogPolicySnapshot?>(exception);
    }
}
