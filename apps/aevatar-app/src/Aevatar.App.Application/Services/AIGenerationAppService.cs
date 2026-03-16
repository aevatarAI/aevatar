using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;

namespace Aevatar.App.Application.Services;

public sealed class AIGenerationAppService : IAIGenerationAppService
{
    private readonly IWorkflowRunCommandService _workflow;
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IFallbackContent _fallbackContent;
    private readonly ILogger<AIGenerationAppService> _logger;

    public AIGenerationAppService(
        IWorkflowRunCommandService workflow,
        IConnectorRegistry connectorRegistry,
        IFallbackContent fallbackContent,
        ILogger<AIGenerationAppService> logger)
    {
        _workflow = workflow;
        _connectorRegistry = connectorRegistry;
        _fallbackContent = fallbackContent;
        _logger = logger;
    }

    public async Task<ManifestationResult> GenerateContentAsync(string userGoal, CancellationToken ct = default)
    {
        var prompt = $"{Prompts.ManifestationSystem}\n\n{Prompts.ManifestationUser(userGoal)}";

        try
        {
            var text = await ExecuteWorkflowAsync("garden_content", prompt, ct);
            return ParseManifestationJson(text, userGoal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manifestation generation failed, using fallback");
            return ToManifestationResult(_fallbackContent.GetManifestationFixedFallback(userGoal));
        }
    }

    public async Task<AffirmationResult> GenerateAffirmationAsync(
        string userGoal, string mantra, string plantName, CancellationToken ct = default)
    {
        var prompt = Prompts.Affirmation(mantra, plantName, userGoal);

        try
        {
            var text = await ExecuteWorkflowAsync("garden_affirmation", prompt, ct);

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("Empty affirmation from workflow, using fallback");
                return new AffirmationResult(_fallbackContent.GetAffirmationFallback());
            }

            return new AffirmationResult(text.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Affirmation generation canceled");
            return new AffirmationResult(_fallbackContent.GetAffirmationFallback());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Affirmation generation failed, using fallback");
            return new AffirmationResult(_fallbackContent.GetAffirmationFallback());
        }
    }

    public async Task<ImageResult> GenerateImageAsync(
        string plantName, string plantDescription, string stage, CancellationToken ct = default)
    {
        var prompt = Prompts.PlantImage(plantName, plantDescription, stage);

        try
        {
            var response = await ExecuteConnectorAsync(
                "gemini_imagen",
                "/v1beta/models/gemini-2.5-flash-image:generateContent",
                Prompts.ToGeminiImagePayload(prompt),
                ct);

            var base64 = ExtractGeminiInlineData(response);
            if (base64 == null)
            {
                _logger.LogWarning("No inlineData in Gemini image response, returning placeholder");
                return ImageResult.Placeholder(_fallbackContent.PlaceholderImage);
            }

            return ImageResult.Ok(base64);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image generation failed");
            var message = ex.Message;
            var reason = message.Contains("429", StringComparison.Ordinal)
                         || message.Contains("rate", StringComparison.OrdinalIgnoreCase)
                ? "rate_limit"
                : "unknown";
            return ImageResult.Fail(reason, "Image generation failed");
        }
    }

    public async Task<SpeechResult> GenerateSpeechAsync(string text, CancellationToken ct = default)
    {
        var prompt = Prompts.Speech(text);

        try
        {
            var response = await ExecuteConnectorAsync(
                "gemini_tts",
                "/v1beta/models/gemini-2.5-flash-preview-tts:generateContent",
                Prompts.ToGeminiSpeechPayload(prompt),
                ct);

            var base64 = ExtractGeminiInlineData(response);
            if (base64 == null)
                return SpeechResult.Fail("no_audio", "Speech generation returned no data");

            return SpeechResult.Ok(base64);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech generation failed");
            var reason = ex.Message.Contains("429", StringComparison.Ordinal) ? "rate_limit" : "unknown";
            return SpeechResult.Fail(reason, "Speech generation failed");
        }
    }

    private async Task<string> ExecuteConnectorAsync(
        string connectorName, string path, string payload, CancellationToken ct)
    {
        if (!_connectorRegistry.TryGet(connectorName, out var connector) || connector == null)
            throw new InvalidOperationException($"Connector '{connectorName}' not found");

        var request = new ConnectorRequest
        {
            Connector = connectorName,
            Operation = path,
            Payload = payload,
            Parameters = new Dictionary<string, string>
            {
                ["path"] = path,
                ["timeout_ms"] = "90000",
            },
        };

        var response = await connector.ExecuteAsync(request, ct);
        if (!response.Success)
            throw new InvalidOperationException($"Connector '{connectorName}' failed: {response.Error}");

        return response.Output;
    }

    private static string? ExtractGeminiInlineData(string geminiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(geminiJson);
            var candidates = doc.RootElement.GetProperty("candidates");
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out var content)) continue;
                if (!content.TryGetProperty("parts", out var parts)) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("inlineData", out var inlineData) &&
                        inlineData.TryGetProperty("data", out var data))
                        return data.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private async Task<string> ExecuteWorkflowAsync(string workflowName, string prompt, CancellationToken ct)
    {
        var request = new WorkflowChatRunRequest(prompt, workflowName, ActorId: null);
        var sb = new StringBuilder();

        var result = await _workflow.ExecuteAsync(
            request,
            (frame, _) =>
            {
                if (frame.Delta is not null)
                    sb.Append(frame.Delta);
                return ValueTask.CompletedTask;
            },
            ct: ct);

        if (!result.Succeeded)
            throw new WorkflowExecutionException(workflowName, result.Error);

        return sb.ToString();
    }

    private ManifestationResult ParseManifestationJson(string text, string userGoal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Empty manifestation response, using random fallback");
            return ToManifestationResult(_fallbackContent.GetManifestationFallback());
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var mantra = root.TryGetProperty("mantra", out var m) ? m.GetString() : null;
            var plantName = root.TryGetProperty("plantName", out var n) ? n.GetString() : null;
            var plantDescription = root.TryGetProperty("plantDescription", out var d) ? d.GetString() : null;

            if (string.IsNullOrWhiteSpace(mantra) || string.IsNullOrWhiteSpace(plantName) ||
                string.IsNullOrWhiteSpace(plantDescription))
            {
                _logger.LogWarning("Missing fields in manifestation JSON, using random fallback");
                return ToManifestationResult(_fallbackContent.GetManifestationFallback());
            }

            return new ManifestationResult(mantra, plantName, plantDescription);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid manifestation JSON, using random fallback");
            return ToManifestationResult(_fallbackContent.GetManifestationFallback());
        }
    }

    private static ManifestationResult ToManifestationResult(ManifestationFallback fb) =>
        new(fb.Mantra, fb.PlantName, fb.PlantDescription);
}

public sealed record ManifestationResult(string Mantra, string PlantName, string PlantDescription);

public sealed record AffirmationResult(string Affirmation);

public sealed record ImageResult(
    string? ImageData,
    bool IsPlaceholder,
    bool Succeeded,
    string? Reason,
    string? Message)
{
    public static ImageResult Ok(string imageData) =>
        new(imageData, IsPlaceholder: false, Succeeded: true, Reason: null, Message: null);

    public static ImageResult Placeholder(string imageData) =>
        new(imageData, IsPlaceholder: true, Succeeded: true, Reason: null, Message: null);

    public static ImageResult Fail(string reason, string message) =>
        new(null, IsPlaceholder: false, Succeeded: false, Reason: reason, Message: message);
}

public sealed record SpeechResult(string? AudioData, bool Succeeded, string? Reason, string? Message)
{
    public static SpeechResult Ok(string audioData) => new(audioData, true, null, null);
    public static SpeechResult Fail(string reason, string message) => new(null, false, reason, message);
}

public sealed class WorkflowExecutionException : Exception
{
    public string WorkflowName { get; }
    public WorkflowChatRunStartError ErrorCode { get; }

    public WorkflowExecutionException(string workflowName, WorkflowChatRunStartError error)
        : base($"Workflow '{workflowName}' failed with error: {error}")
    {
        WorkflowName = workflowName;
        ErrorCode = error;
    }
}
