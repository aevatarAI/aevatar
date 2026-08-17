using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class LLMModelDiscoveryApplicationServiceTests
{
    [Fact]
    public async Task ListModelsAsync_ShouldDeriveSortedEntriesFromExplicitPolicy()
    {
        var service = CreateService(new StubPolicyQueryPort(
            ScopePolicy(
            [
                UserSource("user-beta", "beta", "model-b"),
                UserSource("user-alpha", "alpha", "model-z", "model-a"),
            ])));

        var models = await service.ListModelsAsync("scope-alpha");

        models.Should().Equal(
            Descriptor("alpha/model-a", "alpha"),
            Descriptor("alpha/model-z", "alpha"),
            Descriptor("beta/model-b", "beta"));
    }

    [Fact]
    public async Task ListModelsAsync_ShouldUseInheritedPlatformPolicy()
    {
        var service = CreateService(new StubPolicyQueryPort(
            ScopePolicy([], LLMModelCatalogPolicyMode.InheritPlatform),
            PlatformPolicy(
            [
                CatalogSource("catalog-alpha", "alpha", "model-a"),
            ])));

        var models = await service.ListModelsAsync("scope-alpha");

        models.Should().Equal(Descriptor("alpha/model-a", "alpha"));
    }

    [Fact]
    public async Task ListModelsAsync_WhenEffectivePolicyIsExplicitlyEmpty_ShouldReturnEmpty()
    {
        var service = CreateService(new StubPolicyQueryPort(ScopePolicy([])));

        var models = await service.ListModelsAsync("scope-alpha");

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task ListModelsAsync_WhenPolicyReadFails_ShouldReturnUnavailableError()
    {
        var sourceFailure = new InvalidOperationException("projection unavailable");
        var service = CreateService(new FailingPolicyQueryPort(sourceFailure));

        var act = () => service.ListModelsAsync("scope-alpha");

        var thrown = await act.Should().ThrowAsync<LLMModelCatalogApplicationException>();
        thrown.Which.Kind.Should().Be(LLMModelCatalogApplicationErrorKind.Unavailable);
        thrown.Which.Code.Should().Be("MODEL_CATALOG_UNAVAILABLE");
        thrown.Which.InnerException.Should().BeSameAs(sourceFailure);
    }

    [Fact]
    public async Task ListModelsAsync_WhenEffectivePlatformProjectionIsMissing_ShouldReturnUnavailableError()
    {
        var service = CreateService(new StubPolicyQueryPort(
            ScopePolicy([], LLMModelCatalogPolicyMode.InheritPlatform)));

        var act = () => service.ListModelsAsync("scope-alpha");

        var thrown = await act.Should().ThrowAsync<LLMModelCatalogApplicationException>();
        thrown.Which.Kind.Should().Be(LLMModelCatalogApplicationErrorKind.Unavailable);
        thrown.Which.Code.Should().Be("MODEL_CATALOG_UNAVAILABLE");
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ListModelsAsync_WhenCallerCancelsPolicyRead_ShouldPreserveCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sourceFailure = new OperationCanceledException(cancellation.Token);
        var service = CreateService(new FailingPolicyQueryPort(sourceFailure));

        var act = () => service.ListModelsAsync("scope-alpha", cancellation.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.Should().BeSameAs(sourceFailure);
    }

    private static LLMModelDiscoveryApplicationService CreateService(
        ILLMModelCatalogPolicyQueryPort queryPort) =>
        new(new LLMModelSourceResolver(queryPort));

    private static LLMModelDescriptor Descriptor(string id, string serviceSlug) =>
        new(
            Id: id,
            Created: 0,
            OwnedBy: serviceSlug,
            Group: serviceSlug,
            ContextLength: null,
            MaxOutputTokens: null,
            DisplayName: null,
            Description: null);

    private static LLMModelCatalogPolicySnapshot ScopePolicy(
        IReadOnlyList<LLMModelCatalogPolicySource> sources,
        LLMModelCatalogPolicyMode mode = LLMModelCatalogPolicyMode.Custom) =>
        new(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
            mode,
            sources,
            StateVersion: 1,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch);

    private static LLMModelCatalogPolicySnapshot PlatformPolicy(
        IReadOnlyList<LLMModelCatalogPolicySource> sources) =>
        new(
            LLMModelCatalogPolicyOwner.Platform,
            LLMModelCatalogPolicyMode.Custom,
            sources,
            StateVersion: 1,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch);

    private static LLMModelCatalogPolicySource UserSource(
        string userServiceId,
        string serviceSlug,
        params string[] modelIds) =>
        new(
            new NyxIDUserServiceModelSourceIdentity(userServiceId),
            serviceSlug,
            new ExplicitLLMModels(modelIds));

    private static LLMModelCatalogPolicySource CatalogSource(
        string catalogServiceId,
        string serviceSlug,
        params string[] modelIds) =>
        new(
            new NyxIDCatalogServiceModelSourceIdentity(catalogServiceId),
            serviceSlug,
            new ExplicitLLMModels(modelIds));

    private sealed class StubPolicyQueryPort(
        LLMModelCatalogPolicySnapshot? scopePolicy,
        LLMModelCatalogPolicySnapshot? platformPolicy = null) : ILLMModelCatalogPolicyQueryPort
    {
        public Task<LLMModelCatalogPolicySnapshot?> GetAsync(
            LLMModelCatalogPolicyOwner owner,
            CancellationToken ct = default) =>
            Task.FromResult(owner.Kind == LLMModelCatalogPolicyOwnerKind.Scope
                ? scopePolicy
                : platformPolicy);
    }

    private sealed class FailingPolicyQueryPort(Exception exception) : ILLMModelCatalogPolicyQueryPort
    {
        public Task<LLMModelCatalogPolicySnapshot?> GetAsync(
            LLMModelCatalogPolicyOwner owner,
            CancellationToken ct = default) =>
            Task.FromException<LLMModelCatalogPolicySnapshot?>(exception);
    }
}
