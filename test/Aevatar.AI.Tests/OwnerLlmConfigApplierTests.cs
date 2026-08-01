using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.LLMProviders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class OwnerLlmConfigApplierTests
{
    [Fact]
    public async Task ApplyAsync_WithExplicitModel_ShouldApplyExactRouteAndModel()
    {
        var source = new StubSource(new OwnerLlmConfig(
            UserServiceSelection("gpt-5.5"),
            LLMSelectionPersistenceStatus.Ready,
            7));

        var applied = await OwnerLlmConfigApplier.ApplyAsync(
            LLMControlContext.Empty,
            "scope-alpha",
            source,
            NullLogger.Instance,
            "test",
            "actor-alpha",
            default);

        applied.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm-public");
        applied.ModelOverride.Should().Be("gpt-5.5");
        applied.MaxToolRoundsOverride.Should().Be(7);
    }

    [Fact]
    public async Task ApplyAsync_WithProviderDefault_ShouldApplyRouteOnly()
    {
        var current = LLMControlContext.Empty with { ModelOverride = "request-model" };
        var source = new StubSource(new OwnerLlmConfig(
            UserServiceSelection(null),
            LLMSelectionPersistenceStatus.Ready,
            0));

        var applied = await OwnerLlmConfigApplier.ApplyAsync(
            current, "scope-alpha", source, NullLogger.Instance, "test", "actor-alpha", default);

        applied.ModelOverride.Should().Be("request-model");
        applied.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm-public");
    }

    [Fact]
    public async Task ApplyAsync_WithSystemDefault_ShouldPreserveCurrentControl()
    {
        var current = LLMControlContext.Empty with
        {
            ModelOverride = "request-model",
            NyxIdRoutePreference = "/request-route",
        };
        var source = new StubSource(OwnerLlmConfig.Empty);

        var applied = await OwnerLlmConfigApplier.ApplyAsync(
            current, "scope-alpha", source, NullLogger.Instance, "test", "actor-alpha", default);

        applied.Should().Be(current);
    }

    [Fact]
    public async Task ApplyAsync_WithLegacySelection_ShouldStopBeforeLlmCall()
    {
        var source = new StubSource(new OwnerLlmConfig(
            new LLMSelection(),
            LLMSelectionPersistenceStatus.LegacyRepairRequired,
            0));

        var act = () => OwnerLlmConfigApplier.ApplyAsync(
            LLMControlContext.Empty,
            "scope-alpha",
            source,
            NullLogger.Instance,
            "test",
            "actor-alpha",
            default);

        await act.Should().ThrowAsync<LLMSelectionRepairRequiredException>()
            .Where(ex => ex.Code == LLMSelectionRepairRequiredException.StableCode);
    }

    [Fact]
    public async Task ApplyAsync_WhenSourceFails_ShouldPreserveCurrentControl()
    {
        var current = LLMControlContext.Empty with { ModelOverride = "request-model" };

        var applied = await OwnerLlmConfigApplier.ApplyAsync(
            current,
            "scope-alpha",
            new StubSource(throwOnGet: true),
            NullLogger.Instance,
            "test",
            "actor-alpha",
            default);

        applied.Should().Be(current);
    }

    private static LLMSelection UserServiceSelection(string? modelId) => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = "/api/v1/proxy/s/chrono-llm-public",
        NyxIdUserServiceId = "us-alpha",
        ServiceSlugSnapshot = "chrono-llm-public",
        ModelSelection = new LLMModelSelection
        {
            Kind = modelId is null
                ? LLMModelSelectionKind.ProviderDefault
                : LLMModelSelectionKind.ExplicitModel,
            ModelId = modelId ?? string.Empty,
        },
    };

    private sealed class StubSource(OwnerLlmConfig? config = null, bool throwOnGet = false)
        : IOwnerLlmConfigSource
    {
        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default)
        {
            if (throwOnGet)
                throw new InvalidOperationException("projection unavailable");

            return Task.FromResult(config ?? OwnerLlmConfig.Empty);
        }
    }
}
