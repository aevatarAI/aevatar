using FluentAssertions;
using Aevatar.App.Application.Services;

namespace Aevatar.App.Application.Tests;

public sealed class PromptTests
{
    [Fact]
    public void ManifestationUser_InterpolatesGoal()
    {
        var prompt = Prompts.ManifestationUser("get healthy");
        prompt.Should().Contain("get healthy");
    }

    [Fact]
    public void Affirmation_InterpolatesAllFields()
    {
        var prompt = Prompts.Affirmation("be strong", "Oak of Resolve", "build muscle");

        prompt.Should().Contain("be strong");
        prompt.Should().Contain("Oak of Resolve");
        prompt.Should().Contain("build muscle");
    }

    [Theory]
    [InlineData("seed", "magical seed")]
    [InlineData("sprout", "adorable sprout")]
    [InlineData("growing", "growing magical plant")]
    [InlineData("blooming", "fully bloomed")]
    public void PlantImage_StageSpecificPrompt(string stage, string expected)
    {
        var prompt = Prompts.PlantImage("Fern", "a green fern", stage);

        prompt.Should().Contain(expected);
        prompt.Should().Contain("Fern");
        prompt.Should().Contain(Prompts.ImageStyle);
    }

    [Fact]
    public void PlantImage_GrowingAndBlooming_IncludeDescription()
    {
        Prompts.PlantImage("Fern", "a green fern", "growing").Should().Contain("a green fern");
        Prompts.PlantImage("Fern", "a green fern", "blooming").Should().Contain("a green fern");
    }

    [Fact]
    public void PlantImage_UnknownStage_FallsBackToDefault()
    {
        var prompt = Prompts.PlantImage("Fern", "a fern", "unknown");
        prompt.Should().Contain("magical Fern plant");
    }
}
