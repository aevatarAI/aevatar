using FluentAssertions;
using Aevatar.App.Application.Services;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Tests;

public sealed class FallbackContentTests
{
    private static FallbackContent CreateService(Action<FallbackOptions>? configure = null)
    {
        var options = new FallbackOptions();
        configure?.Invoke(options);
        return new FallbackContent(Options.Create(options));
    }

    [Fact]
    public void Plants_Has5_Entries()
    {
        var fallback = CreateService();

        fallback.Plants.Should().HaveCount(5);
    }

    [Fact]
    public void GetManifestationFallback_ReturnsFromPool()
    {
        var fallback = CreateService();
        var results = Enumerable.Range(0, 50).Select(_ => fallback.GetManifestationFallback()).ToList();

        results.Should().AllSatisfy(r =>
        {
            r.PlantName.Should().NotBeEmpty();
            r.Mantra.Should().NotBeEmpty();
            r.PlantDescription.Should().NotBeEmpty();
        });

        results.Select(r => r.PlantName).Distinct().Count().Should().BeGreaterThan(1,
            "random selection should cover multiple entries");
    }

    [Fact]
    public void GetManifestationFixedFallback_ContainsGoal()
    {
        var fallback = CreateService();
        var fb = fallback.GetManifestationFixedFallback("be happy");

        fb.PlantName.Should().Be("Celestial Potential");
        fb.Mantra.Should().Contain("be happy");
    }

    [Fact]
    public void Affirmations_Has5_Entries()
    {
        var fallback = CreateService();

        fallback.Affirmations.Should().HaveCount(5);
    }

    [Fact]
    public void GetAffirmationFallback_ReturnsFromPool()
    {
        var fallback = CreateService();
        var results = Enumerable.Range(0, 50).Select(_ => fallback.GetAffirmationFallback()).ToList();

        results.Should().AllSatisfy(r => r.Should().NotBeEmpty());
        results.Distinct().Count().Should().BeGreaterThan(1);
    }

    [Fact]
    public void PlaceholderImage_IsValidBase64()
    {
        var fallback = CreateService();
        var act = () => Convert.FromBase64String(fallback.PlaceholderImage);
        act.Should().NotThrow();
    }

    [Fact]
    public void CustomOptions_AreLoaded_WhenProvided()
    {
        var fallback = CreateService(options =>
        {
            options.Content =
            [
                new ManifestationFallbackOptions
                {
                    Name = "Custom Plant",
                    Mantra = "Custom mantra",
                    Description = "Custom description"
                }
            ];
            options.FixedContent = new ManifestationFixedFallbackOptions
            {
                Name = "Fixed Plant",
                MantraTemplate = "Goal: {userGoal}",
                Description = "Fixed description"
            };
            options.Affirmations = ["Custom affirmation"];
            options.FixedAffirmation = "Fixed affirmation";
            options.PlaceholderImage = "dGVzdA==";
        });

        fallback.Plants.Should().ContainSingle();
        fallback.Plants[0].PlantName.Should().Be("Custom Plant");
        fallback.GetManifestationFixedFallback("grow").Mantra.Should().Be("Goal: grow");
        fallback.Affirmations.Should().ContainSingle("Custom affirmation");
        fallback.FixedAffirmation.Should().Be("Fixed affirmation");
        fallback.PlaceholderImage.Should().Be("dGVzdA==");
    }

    [Fact]
    public void InvalidConfiguredValues_FallBackToDefaults()
    {
        var fallback = CreateService(options =>
        {
            options.Content = [new ManifestationFallbackOptions()];
            options.Affirmations = [""];
            options.PlaceholderImage = "invalid_base64";
        });

        fallback.Plants.Should().HaveCount(5);
        fallback.Affirmations.Should().HaveCount(5);

        var act = () => Convert.FromBase64String(fallback.PlaceholderImage);
        act.Should().NotThrow();
    }
}
