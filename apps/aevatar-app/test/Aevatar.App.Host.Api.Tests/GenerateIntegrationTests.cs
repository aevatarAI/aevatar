using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Aevatar.App.Host.Api.Tests;

public sealed class GenerateIntegrationTests : IClassFixture<AppTestFixture>
{
    private readonly AppTestFixture _fx;

    public GenerateIntegrationTests(AppTestFixture fx) => _fx = fx;

    [Fact]
    public async Task GenerateManifestation_ValidGoal_ReturnsContent()
    {
        _fx.WorkflowStub.ShouldFail = false;
        _fx.WorkflowStub.NextResponse = """{"mantra":"Be the light","plantName":"Sunflower","plantDescription":"A golden flower"}""";

        using var client = _fx.CreateAuthenticatedClient("gen-manif", "gen@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/manifestation", new { userGoal = "Find inner peace" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("mantra").GetString().Should().Be("Be the light");
        json.GetProperty("plantName").GetString().Should().Be("Sunflower");
    }

    [Fact]
    public async Task GenerateManifestation_WorkflowFails_ReturnsFallback()
    {
        _fx.WorkflowStub.ShouldFail = true;

        using var client = _fx.CreateAuthenticatedClient("gen-fallback", "fallback@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/manifestation", new { userGoal = "Grow" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("mantra").GetString().Should().NotBeNullOrEmpty("should return fallback content");
        json.GetProperty("plantName").GetString().Should().NotBeNullOrEmpty();

        _fx.WorkflowStub.ShouldFail = false;
    }

    [Fact]
    public async Task GenerateManifestation_EmptyGoal_ReturnsBadRequest()
    {
        using var client = _fx.CreateAuthenticatedClient("gen-empty", "empty@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/manifestation", new { userGoal = "" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GenerateAffirmation_ValidInput_ReturnsText()
    {
        _fx.WorkflowStub.ShouldFail = false;
        _fx.WorkflowStub.NextResponse = "You are growing stronger every day";

        using var client = _fx.CreateAuthenticatedClient("gen-affirm", "affirm@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/affirmation", new
        {
            userGoal = "Grow",
            mantra = "Be strong",
            plantName = "Oak"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("affirmation").GetString().Should().Contain("growing stronger");
    }

    [Fact]
    public async Task GenerateAffirmation_WorkflowFails_ReturnsFallback()
    {
        _fx.WorkflowStub.ShouldFail = true;

        using var client = _fx.CreateAuthenticatedClient("gen-affirm-fb", "affirm-fb@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/affirmation", new
        {
            userGoal = "Grow",
            mantra = "Be strong",
            plantName = "Oak"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("affirmation").GetString().Should().NotBeNullOrEmpty();

        _fx.WorkflowStub.ShouldFail = false;
    }

    [Fact]
    public async Task GenerateAffirmation_InvalidTrigger_ReturnsBadRequest()
    {
        using var client = _fx.CreateAuthenticatedClient("gen-affirm-trigger", "affirm-trigger@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/affirmation", new
        {
            userGoal = "Grow",
            mantra = "Be strong",
            plantName = "Oak",
            trigger = "unknown"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GenerateAffirmation_InvalidStage_ReturnsBadRequest()
    {
        using var client = _fx.CreateAuthenticatedClient("gen-affirm-stage", "affirm-stage@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/affirmation", new
        {
            userGoal = "Grow",
            mantra = "Be strong",
            plantName = "Oak",
            stage = "invalid"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GeneratePlantImage_ValidInput_ReturnsImage()
    {
        _fx.ConnectorStub.NextResponse = """{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"image/png","data":"iVBORw0KGgoAAAANSUhEUg=="}}]}}]}""";

        using var client = _fx.CreateAuthenticatedClient("gen-img", "img@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/plant-image", new
        {
            manifestationId = "m1",
            plantName = "Rose",
            stage = "seed"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("imageData").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GeneratePlantImage_WorkflowFails_ReturnsPlaceholder()
    {
        _fx.ConnectorStub.NextResponse = """{"candidates":[]}""";

        using var client = _fx.CreateAuthenticatedClient("gen-img-fb", "img-fb@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/plant-image", new
        {
            manifestationId = "m1",
            plantName = "Rose",
            stage = "sprout"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("isPlaceholder").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GeneratePlantImage_InvalidStage_ReturnsBadRequest()
    {
        using var client = _fx.CreateAuthenticatedClient("gen-img-bad", "img-bad@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/plant-image", new
        {
            manifestationId = "m1",
            plantName = "Rose",
            stage = "invalid"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GeneratePlantImage_TooLongDescription_ReturnsBadRequest()
    {
        using var client = _fx.CreateAuthenticatedClient("gen-img-long-desc", "img-long-desc@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/plant-image", new
        {
            manifestationId = "m1",
            plantName = "Rose",
            stage = "seed",
            plantDescription = new string('a', 501)
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GenerateSpeech_ValidText_ReturnsAudio()
    {
        _fx.ConnectorStub.NextResponse = """{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"audio/mp3","data":"base64audiodata"}}]}}]}""";

        using var client = _fx.CreateAuthenticatedClient("gen-speech", "speech@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/speech", new { text = "Hello world" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("audioData").GetString().Should().Be("base64audiodata");
    }

    [Fact]
    public async Task GenerateSpeech_WorkflowFails_Returns500()
    {
        _fx.ConnectorStub.ShouldFail = true;

        using var client = _fx.CreateAuthenticatedClient("gen-speech-fail", "speech-fail@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/speech", new { text = "Hello world" });

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        _fx.ConnectorStub.ShouldFail = false;
    }

    [Fact]
    public async Task GenerateSpeech_EmptyText_ReturnsBadRequest()
    {
        using var client = _fx.CreateAuthenticatedClient("gen-speech-empty", "speech-empty@test.com");
        var res = await client.PostAsJsonAsync("/api/generate/speech", new { text = "" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
