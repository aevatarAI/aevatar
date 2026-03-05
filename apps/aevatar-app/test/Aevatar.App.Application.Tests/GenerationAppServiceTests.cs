using Aevatar.App.Application.Services;
using FluentAssertions;

namespace Aevatar.App.Application.Tests;

public sealed class GenerationAppServiceTests
{
    [Fact]
    public async Task GenerateManifestation_Success_ReturnsAiResult()
    {
        var expected = new ManifestationResult("mantra", "Bloom", "A flower");
        var ai = new StubAIGeneration(manifestation: expected);
        var fb = new StubFallback();
        var svc = new GenerationAppService(ai, fb);

        var result = await svc.GenerateManifestationAsync("my goal", CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task GenerateManifestation_AiFails_ReturnsFallback()
    {
        var ai = new StubAIGeneration(throwOnManifestation: true);
        var fb = new StubFallback();
        var svc = new GenerationAppService(ai, fb);

        var result = await svc.GenerateManifestationAsync("grow", CancellationToken.None);

        result.PlantName.Should().Be("Fixed Plant");
        result.Mantra.Should().Contain("grow");
    }

    [Fact]
    public async Task GenerateAffirmation_Success_ReturnsAiResult()
    {
        var expected = new AffirmationResult("You are growing");
        var ai = new StubAIGeneration(affirmation: expected);
        var fb = new StubFallback();
        var svc = new GenerationAppService(ai, fb);

        var result = await svc.GenerateAffirmationAsync("goal", "mantra", "plant", CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task GenerateAffirmation_AiFails_ReturnsFallback()
    {
        var ai = new StubAIGeneration(throwOnAffirmation: true);
        var fb = new StubFallback();
        var svc = new GenerationAppService(ai, fb);

        var result = await svc.GenerateAffirmationAsync("goal", "m", "p", CancellationToken.None);

        result.Affirmation.Should().Be("Fixed affirmation");
    }
}

file sealed class StubAIGeneration : IAIGenerationAppService
{
    private readonly ManifestationResult? _manifestation;
    private readonly AffirmationResult? _affirmation;
    private readonly bool _throwOnManifestation;
    private readonly bool _throwOnAffirmation;

    public StubAIGeneration(
        ManifestationResult? manifestation = null,
        AffirmationResult? affirmation = null,
        bool throwOnManifestation = false,
        bool throwOnAffirmation = false)
    {
        _manifestation = manifestation;
        _affirmation = affirmation;
        _throwOnManifestation = throwOnManifestation;
        _throwOnAffirmation = throwOnAffirmation;
    }

    public Task<ManifestationResult> GenerateContentAsync(string userGoal, CancellationToken ct)
    {
        if (_throwOnManifestation) throw new InvalidOperationException("AI unavailable");
        return Task.FromResult(_manifestation!);
    }

    public Task<AffirmationResult> GenerateAffirmationAsync(
        string userGoal, string mantra, string plantName, CancellationToken ct)
    {
        if (_throwOnAffirmation) throw new InvalidOperationException("AI unavailable");
        return Task.FromResult(_affirmation!);
    }

    public Task<ImageResult> GenerateImageAsync(
        string plantName, string plantDescription, string stage, CancellationToken ct)
        => Task.FromResult(ImageResult.Placeholder("placeholder"));

    public Task<SpeechResult> GenerateSpeechAsync(string text, CancellationToken ct)
        => Task.FromResult(SpeechResult.Fail("stub", "not implemented"));
}

file sealed class StubFallback : IFallbackContent
{
    public IReadOnlyList<ManifestationFallback> Plants { get; } =
        [new("Random Plant", "random mantra", "random desc")];

    public IReadOnlyList<string> Affirmations { get; } = ["Fixed affirmation"];
    public string FixedAffirmation => "Fixed affirmation";
    public string PlaceholderImage => "AAAA";

    public ManifestationFallback GetManifestationFallback() => Plants[0];

    public ManifestationFallback GetManifestationFixedFallback(string userGoal)
        => new("Fixed Plant", $"I am manifesting: {userGoal}", "Fixed desc");

    public string GetAffirmationFallback() => FixedAffirmation;
}
