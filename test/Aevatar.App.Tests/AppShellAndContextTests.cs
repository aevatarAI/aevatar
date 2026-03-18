namespace Aevatar.App.Tests;

[Collection("AppHost")]
public sealed class AppShellAndContextTests
{
    private readonly AppHostFixture _fixture;

    public AppShellAndContextTests(AppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HealthEndpoint_ShouldReportEmbeddedAppHost()
    {
        using var health = await _fixture.GetJsonAsync("/api/app/health");

        health.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        health.RootElement.GetProperty("service").GetString().Should().Be("aevatar.app");
        health.RootElement.GetProperty("mode").GetString().Should().Be("embedded");
        health.RootElement.GetProperty("sdkBaseUrl").GetString().Should().Be(_fixture.BaseUrl);
    }

    [Fact]
    public async Task RootShell_AndContext_ShouldExposeStudioCapabilities()
    {
        var html = await _fixture.Client.GetStringAsync("/");
        html.Should().Contain("<title>Aevatar Workflow Studio</title>");
        html.Should().Contain("<div id=\"root\"></div>");
        html.Should().Contain("/app.js");

        using var context = await _fixture.GetJsonAsync("/api/app/context");
        context.RootElement.GetProperty("mode").GetString().Should().Be("embedded");
        context.RootElement.GetProperty("features").GetProperty("publishedWorkflows").GetBoolean().Should().BeTrue();
        context.RootElement.GetProperty("features").GetProperty("scripts").GetBoolean().Should().BeTrue();
        context.RootElement.GetProperty("scriptContract").GetProperty("inputType").GetString()
            .Should().Be("type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand");
        context.RootElement.GetProperty("scriptContract").GetProperty("readModelFields").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal("input", "output", "status", "last_command_id", "notes");
    }

    [Fact]
    public async Task AuthAndGeneratorValidationEndpoints_ShouldReturnExpectedGuardResponses()
    {
        using var auth = await _fixture.GetJsonAsync("/api/auth/me");
        auth.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
        auth.RootElement.GetProperty("authenticated").GetBoolean().Should().BeFalse();

        using var workflowGenerator = await _fixture.PostJsonAsync("/api/app/workflow-generator", new
        {
            prompt = string.Empty,
        }, HttpStatusCode.BadRequest);
        workflowGenerator.RootElement.GetProperty("code").GetString().Should().Be("WORKFLOW_GENERATOR_PROMPT_REQUIRED");

        using var scriptGenerator = await _fixture.PostJsonAsync("/api/app/scripts/generator", new
        {
            prompt = string.Empty,
        }, HttpStatusCode.BadRequest);
        scriptGenerator.RootElement.GetProperty("code").GetString().Should().Be("SCRIPT_GENERATOR_PROMPT_REQUIRED");
    }
}
