using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class LLMSelectionPolicyTests
{
    [Fact]
    public void LLMSelection_ShouldRoundTripExactRouteAndExplicitModel()
    {
        var selection = UserServiceSelection("gpt-5.5");

        var copy = LLMSelection.Parser.ParseFrom(selection.ToByteArray());

        copy.Should().BeEquivalentTo(selection);
        LLMSelectionPolicy.ValidateSelection(copy);
        LLMSelectionPolicy.CompatibilityDefaultModel(copy).Should().Be("gpt-5.5");
        LLMSelectionPolicy.CompatibilityRoute(copy)
            .Should().Be("/api/v1/proxy/s/chrono-llm-public");
    }

    [Theory]
    [InlineData(" model-a")]
    [InlineData("model-a ")]
    [InlineData("model\u0001a")]
    public void LLMSelection_ShouldRejectNonCanonicalExplicitModel(string modelId)
    {
        var selection = GatewaySelection(LLMModelSelectionKind.ExplicitModel, modelId);

        FluentActions.Invoking(() => LLMSelectionPolicy.ValidateSelection(selection))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LLMSelection_ShouldRejectExplicitModelOverUtf8Limit()
    {
        var selection = GatewaySelection(
            LLMModelSelectionKind.ExplicitModel,
            new string('a', LLMSelectionPolicy.MaxModelIdUtf8Bytes + 1));

        FluentActions.Invoking(() => LLMSelectionPolicy.ValidateSelection(selection))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LLMSelection_ShouldAcceptCompleteUnspecifiedSelection()
    {
        var selection = LLMSelectionPolicy.SystemDefaultSelection();

        LLMSelectionPolicy.ValidateSelection(selection);
        LLMSelectionPolicy.CompatibilityDefaultModel(selection).Should().BeEmpty();
        LLMSelectionPolicy.CompatibilityRoute(selection).Should().BeEmpty();
    }

    [Fact]
    public void LLMSelection_ShouldAcceptGatewayProviderDefault()
    {
        var selection = GatewaySelection(LLMModelSelectionKind.ProviderDefault);

        LLMSelectionPolicy.ValidateSelection(selection);
        LLMSelectionPolicy.CompatibilityDefaultModel(selection).Should().BeEmpty();
        LLMSelectionPolicy.CompatibilityRoute(selection).Should().Be(LLMSelectionPolicy.GatewayRoute);
    }

    [Fact]
    public void LLMModelCatalog_ShouldNotTreatEmptyEnumerationAsOpen()
    {
        var catalog = LLMSelectionPolicy.NormalizeCatalog(
            [],
            null,
            LLMModelCatalogDiagnosticKind.NotPublished);

        catalog.Certainty.Should().Be(LLMModelCatalogCertainty.NotVerifiable);
        catalog.ModelIds.Should().BeEmpty();
    }

    [Fact]
    public void LLMModelCatalog_ShouldRejectMoreThanMaximumDistinctModels()
    {
        var models = Enumerable.Range(0, LLMSelectionPolicy.MaxModelsPerCatalog + 1)
            .Select(index => $"model-{index:D4}");

        FluentActions.Invoking(() => LLMSelectionPolicy.NormalizeCatalog(
                models,
                null,
                LLMModelCatalogDiagnosticKind.NotPublished))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LLMModelCatalog_ShouldDeduplicateWithOrdinalEquality()
    {
        var catalog = LLMSelectionPolicy.NormalizeCatalog(
            ["model-a", "MODEL-A", "model-a"],
            "model-a",
            LLMModelCatalogDiagnosticKind.NotPublished);

        catalog.ModelIds.Should().Equal("MODEL-A", "model-a");
        catalog.DefaultModelId.Should().Be("model-a");
    }

    [Fact]
    public void LLMModelCatalog_ShouldRejectDefaultOutsideExactEnumeration()
    {
        FluentActions.Invoking(() => LLMSelectionPolicy.NormalizeCatalog(
                ["model-a"],
                "MODEL-A",
                LLMModelCatalogDiagnosticKind.NotPublished))
            .Should().Throw<InvalidOperationException>();
    }

    private static LLMSelection GatewaySelection(
        LLMModelSelectionKind kind,
        string modelId = "") => new()
        {
            RouteKind = LLMRouteKind.Gateway,
            RouteValue = LLMSelectionPolicy.GatewayRoute,
            ModelSelection = new LLMModelSelection
            {
                Kind = kind,
                ModelId = modelId,
            },
        };

    private static LLMSelection UserServiceSelection(string modelId) => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = "/api/v1/proxy/s/chrono-llm-public",
        NyxIdUserServiceId = "us-alpha",
        ServiceSlugSnapshot = "chrono-llm-public",
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.ExplicitModel,
            ModelId = modelId,
        },
    };
}
