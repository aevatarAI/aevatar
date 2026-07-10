namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Host parsed OpenAI Responses JSON and then Application parsed the same raw JsonElement payloads again.
//   New principle: Host owns protocol JSON conversion; Application normalizes a typed command shape before session/routing/LLM orchestration.
public static class ResponsesRequestNormalizer
{
    public static ResponsesRequestNormalizationResult Normalize(
        ResponsesCommandRequest request,
        string? defaultModel = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            model = defaultModel?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return ResponsesRequestNormalizationResult.Failed("model_required", "model is required.");

        if (request.MaxOutputTokens is <= 0)
        {
            return ResponsesRequestNormalizationResult.Failed(
                "invalid_max_output_tokens",
                "max_output_tokens must be greater than zero when provided.");
        }

        var prompt = NormalizePrompt(request.Prompt);
        var toolResults = request.ToolResults
            .Where(static result => !string.IsNullOrWhiteSpace(result.CallId))
            .Select(static result => result with
            {
                CallId = result.CallId.Trim(),
                SchemaHash = NormalizeOptional(result.SchemaHash),
            })
            .ToArray();
        var previousResponseId = NormalizeOptional(request.PreviousResponseId);

        if (string.IsNullOrWhiteSpace(prompt) && toolResults.Length == 0)
            return ResponsesRequestNormalizationResult.Failed("invalid_input", "input must contain at least one text value.");

        if (previousResponseId is null && toolResults.Length > 0)
        {
            var foldedSections = new List<string>();
            if (!string.IsNullOrWhiteSpace(prompt))
                foldedSections.Add(prompt);
            foreach (var result in toolResults)
            {
                var marker = $"[tool_result call_id={result.CallId}]";
                foldedSections.Add(string.IsNullOrWhiteSpace(result.Output) ? marker : $"{marker} {result.Output}");
            }
            prompt = string.Join("\n", foldedSections);
            toolResults = [];
        }

        return ResponsesRequestNormalizationResult.Success(new NormalizedResponsesRequest(
            ResponseId: ResponsesIds.NewResponseId(),
            MessageItemId: ResponsesIds.NewMessageId(),
            Model: model,
            Prompt: prompt ?? string.Empty,
            Stream: request.Stream == true,
            PreviousResponseId: previousResponseId,
            Temperature: request.Temperature,
            MaxOutputTokens: request.MaxOutputTokens,
            DeclaredTools: request.DeclaredTools,
            ToolResults: toolResults));
    }

    private static string? NormalizePrompt(string? value)
    {
        var parts = value?
            .Split('\n')
            .Select(static part => part.Trim())
            .Where(static part => part.Length > 0);
        var prompt = parts is null ? null : string.Join("\n", parts);
        return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public static class ResponsesToolSchemaHashes
{
    public static string Compute(string parametersJson)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(parametersJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
