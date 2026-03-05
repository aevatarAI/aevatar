using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Aevatar.App.Application.Services;

namespace Aevatar.App.Application.Tests;

public sealed class AIGenerationAppServiceTests
{
    private static AIGenerationAppService CreateService(
        StubWorkflowService workflow,
        StubConnectorRegistry? connectors = null) =>
        new(workflow,
            connectors ?? new StubConnectorRegistry(),
            CreateFallbackContent(),
            NullLogger<AIGenerationAppService>.Instance);

    private static FallbackContent CreateFallbackContent()
        => new(Options.Create(new FallbackOptions()));

    [Fact]
    public async Task GenerateContent_ParsesValidJson()
    {
        var wf = new StubWorkflowService("""{"mantra":"Be bold","plantName":"Ember Fern","plantDescription":"A glowing fern"}""");
        var svc = CreateService(wf);

        var result = await svc.GenerateContentAsync("get promoted");

        result.Mantra.Should().Be("Be bold");
        result.PlantName.Should().Be("Ember Fern");
        result.PlantDescription.Should().Be("A glowing fern");
    }

    [Fact]
    public async Task GenerateContent_InvalidJson_ReturnsFallback()
    {
        var wf = new StubWorkflowService("not json at all");
        var svc = CreateService(wf);

        var result = await svc.GenerateContentAsync("health");

        result.PlantName.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateContent_MissingFields_ReturnsFallback()
    {
        var wf = new StubWorkflowService("""{"mantra":"ok"}""");
        var svc = CreateService(wf);

        var result = await svc.GenerateContentAsync("health");

        result.PlantName.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateContent_WorkflowError_ReturnsFixedFallback()
    {
        var wf = new StubWorkflowService(error: WorkflowChatRunStartError.WorkflowNotFound);
        var svc = CreateService(wf);

        var result = await svc.GenerateContentAsync("my goal");

        result.PlantName.Should().Be("Celestial Potential");
        result.Mantra.Should().Contain("my goal");
    }

    [Fact]
    public async Task GenerateAffirmation_ReturnsText()
    {
        var wf = new StubWorkflowService("Your garden blooms with every kind thought.");
        var svc = CreateService(wf);

        var result = await svc.GenerateAffirmationAsync("grow", "be kind", "Lotus");

        result.Affirmation.Should().Be("Your garden blooms with every kind thought.");
    }

    [Fact]
    public async Task GenerateAffirmation_Empty_ReturnsFallback()
    {
        var wf = new StubWorkflowService("");
        var svc = CreateService(wf);

        var result = await svc.GenerateAffirmationAsync("grow", "be kind", "Lotus");

        result.Affirmation.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateAffirmation_WorkflowFails_ReturnsFallback()
    {
        var wf = new StubWorkflowService(error: WorkflowChatRunStartError.WorkflowNotFound);
        var svc = CreateService(wf);

        var result = await svc.GenerateAffirmationAsync("grow", "be kind", "Lotus");

        result.Affirmation.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateImage_ReturnsImageData()
    {
        var geminiResponse = GeminiResponseJson("base64imagedata");
        var connectors = new StubConnectorRegistry(geminiResponse);
        var svc = CreateService(new StubWorkflowService(), connectors);

        var result = await svc.GenerateImageAsync("Rose", "a rose", "seed");

        result.ImageData.Should().Be("base64imagedata");
        result.IsPlaceholder.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateImage_NoInlineData_ReturnsPlaceholder()
    {
        var connectors = new StubConnectorRegistry("""{"candidates":[{"content":{"parts":[{"text":"hi"}]}}]}""");
        var svc = CreateService(new StubWorkflowService(), connectors);

        var result = await svc.GenerateImageAsync("Rose", "a rose", "seed");

        result.IsPlaceholder.Should().BeTrue();
        result.ImageData.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateImage_ConnectorMissing_ReturnsFail()
    {
        var connectors = new StubConnectorRegistry();
        var svc = CreateService(new StubWorkflowService(), connectors);

        var result = await svc.GenerateImageAsync("Rose", "a rose", "seed");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateSpeech_ReturnsAudioData()
    {
        var geminiResponse = GeminiResponseJson("audiobase64");
        var connectors = new StubConnectorRegistry(geminiResponse);
        var svc = CreateService(new StubWorkflowService(), connectors);

        var result = await svc.GenerateSpeechAsync("Hello world");

        result.Succeeded.Should().BeTrue();
        result.AudioData.Should().Be("audiobase64");
    }

    [Fact]
    public async Task GenerateSpeech_NoInlineData_ReturnsFailure()
    {
        var connectors = new StubConnectorRegistry("""{"candidates":[{"content":{"parts":[{"text":"hi"}]}}]}""");
        var svc = CreateService(new StubWorkflowService(), connectors);

        var result = await svc.GenerateSpeechAsync("Hello");

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be("no_audio");
    }

    [Fact]
    public async Task GenerateSpeech_ConnectorFails_ReturnsFailure()
    {
        var connectors = new StubConnectorRegistry(error: "API error 429");
        var svc = CreateService(new StubWorkflowService(), connectors);

        var result = await svc.GenerateSpeechAsync("Hello");

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Be("rate_limit");
    }

    private static string GeminiResponseJson(string base64Data) =>
        $$$"""{"candidates":[{"content":{"parts":[{"inlineData":{"mimeType":"image/png","data":"{{{base64Data}}}"}}]}}]}""";
}

internal sealed class StubConnectorRegistry : IConnectorRegistry
{
    private readonly Dictionary<string, IConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);

    public StubConnectorRegistry(string? successOutput = null, string? error = null)
    {
        if (successOutput != null || error != null)
        {
            var connector = new StubConnector(successOutput, error);
            _connectors["gemini_imagen"] = connector;
            _connectors["gemini_tts"] = connector;
        }
    }

    public void Register(IConnector connector) => _connectors[connector.Name] = connector;

    public bool TryGet(string name, out IConnector? connector) => _connectors.TryGetValue(name, out connector);

    public IReadOnlyList<string> ListNames() => _connectors.Keys.ToList();
}

internal sealed class StubConnector : IConnector
{
    private readonly string? _output;
    private readonly string? _error;

    public StubConnector(string? output = null, string? error = null)
    {
        _output = output;
        _error = error;
    }

    public string Name => "stub";
    public string Type => "stub";

    public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
    {
        if (_error != null)
            return Task.FromResult(new ConnectorResponse { Success = false, Error = _error });

        return Task.FromResult(new ConnectorResponse { Success = true, Output = _output ?? "" });
    }
}

internal sealed class StubWorkflowService : IWorkflowRunCommandService
{
    private readonly string? _deltaOutput;
    private readonly WorkflowChatRunStartError _error;

    public StubWorkflowService(string? deltaOutput = null,
        WorkflowChatRunStartError error = WorkflowChatRunStartError.None)
    {
        _deltaOutput = deltaOutput;
        _error = error;
    }

    public async Task<WorkflowChatRunExecutionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
        CancellationToken ct = default)
    {
        if (_error != WorkflowChatRunStartError.None)
            return new WorkflowChatRunExecutionResult(_error, null, null);

        if (_deltaOutput is not null)
        {
            await emitAsync(new WorkflowOutputFrame
            {
                Type = "delta",
                Delta = _deltaOutput,
            }, ct);
        }

        return new WorkflowChatRunExecutionResult(
            WorkflowChatRunStartError.None,
            new WorkflowChatRunStarted("actor-1", request.WorkflowName ?? "test", "cmd-1"),
            new WorkflowChatRunFinalizeResult(WorkflowProjectionCompletionStatus.Completed, true));
    }
}
