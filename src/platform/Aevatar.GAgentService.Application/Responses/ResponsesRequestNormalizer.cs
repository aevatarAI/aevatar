using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Aevatar.GAgentService.Application.Responses;

public static class ResponsesRequestNormalizer
{
    public static ResponsesRequestNormalizationResult Normalize(ResponsesCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return ResponsesRequestNormalizationResult.Failed("model_required", "model is required.");

        if (!TryExtractDeclaredTools(request.Tools, out var declaredTools, out var toolsError))
            return ResponsesRequestNormalizationResult.Failed("invalid_tools", toolsError);

        if (!TryExtractInput(request.Input, out var prompt, out var toolResults, out var inputError))
            return ResponsesRequestNormalizationResult.Failed("invalid_input", inputError);

        if (request.MaxOutputTokens is <= 0)
        {
            return ResponsesRequestNormalizationResult.Failed(
                "invalid_max_output_tokens",
                "max_output_tokens must be greater than zero when provided.");
        }

        var previousResponseId = NormalizeOptional(request.PreviousResponseId);

        if (previousResponseId is null && toolResults.Count > 0)
        {
            var foldedSections = new List<string>();
            if (!string.IsNullOrWhiteSpace(prompt))
                foldedSections.Add(prompt);
            foreach (var tr in toolResults)
            {
                var marker = $"[tool_result call_id={tr.CallId}]";
                foldedSections.Add(string.IsNullOrWhiteSpace(tr.Output) ? marker : $"{marker} {tr.Output}");
            }
            prompt = string.Join("\n", foldedSections);
            toolResults = [];
        }

        return ResponsesRequestNormalizationResult.Success(new NormalizedResponsesRequest(
            ResponseId: ResponsesIds.NewResponseId(),
            MessageItemId: ResponsesIds.NewMessageId(),
            Model: model,
            Prompt: prompt,
            Stream: request.Stream == true,
            PreviousResponseId: previousResponseId,
            Temperature: request.Temperature,
            MaxOutputTokens: request.MaxOutputTokens,
            DeclaredTools: declaredTools,
            ToolResults: toolResults));
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool TryExtractInput(
        JsonElement input,
        [NotNullWhen(true)] out string? prompt,
        out IReadOnlyList<ResponsesToolResultInput> toolResults,
        [NotNullWhen(false)] out string? error)
    {
        var parts = new List<string>();
        var results = new List<ResponsesToolResultInput>();
        ExtractInput(input, parts, results);

        prompt = string.Join("\n", parts.Select(static x => x.Trim()).Where(static x => x.Length > 0));
        toolResults = results;
        if (prompt.Length > 0 || results.Count > 0)
        {
            error = null;
            return true;
        }

        error = "input must contain at least one text value.";
        prompt = null;
        toolResults = [];
        return false;
    }

    private static void ExtractInput(
        JsonElement element,
        ICollection<string> parts,
        ICollection<ResponsesToolResultInput> toolResults)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AddText(element.GetString(), parts);
                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExtractInput(item, parts, toolResults);
                return;

            case JsonValueKind.Object:
                ExtractObjectInput(element, parts, toolResults);
                return;
        }
    }

    private static void ExtractObjectInput(
        JsonElement element,
        ICollection<string> parts,
        ICollection<ResponsesToolResultInput> toolResults)
    {
        if (TryExtractToolResult(element, out var toolResult))
        {
            toolResults.Add(toolResult);
            return;
        }

        if (element.TryGetProperty("text", out var text))
        {
            ExtractInput(text, parts, toolResults);
            return;
        }

        if (element.TryGetProperty("content", out var content))
        {
            ExtractInput(content, parts, toolResults);
            return;
        }

        if (element.TryGetProperty("input_text", out var inputText))
        {
            ExtractInput(inputText, parts, toolResults);
        }
    }

    private static void AddText(string? value, ICollection<string> parts)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parts.Add(value);
    }

    private static bool TryExtractToolResult(
        JsonElement element,
        [NotNullWhen(true)] out ResponsesToolResultInput? toolResult)
    {
        toolResult = null;
        var type = GetStringProperty(element, "type");
        if (!string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var callId = GetStringProperty(element, "call_id")
                     ?? GetStringProperty(element, "tool_call_id")
                     ?? GetStringProperty(element, "id");
        if (string.IsNullOrWhiteSpace(callId))
            return false;

        string? output = null;
        if (element.TryGetProperty("output", out var outputElement))
            output = ElementToPayloadString(outputElement);
        else if (element.TryGetProperty("result", out var resultElement))
            output = ElementToPayloadString(resultElement);

        var schemaHash = GetStringProperty(element, "schema_hash")
                         ?? GetStringProperty(element, "schemaHash");
        toolResult = new ResponsesToolResultInput(
            callId.Trim(),
            output ?? string.Empty,
            NormalizeOptional(schemaHash));
        return true;
    }

    private static bool TryExtractDeclaredTools(
        JsonElement tools,
        out IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        [NotNullWhen(false)] out string? error)
    {
        declaredTools = [];
        error = null;
        if (tools.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        if (tools.ValueKind != JsonValueKind.Array)
        {
            error = "tools must be an array when provided.";
            return false;
        }

        var result = new List<ResponsesApplicationToolDeclaration>();
        var toolIndex = -1;
        foreach (var tool in tools.EnumerateArray())
        {
            toolIndex++;
            if (tool.ValueKind != JsonValueKind.Object)
            {
                error = $"tool at index {toolIndex} must be an object.";
                return false;
            }

            var toolType = GetStringProperty(tool, "type");
            var isFunctionType = string.IsNullOrWhiteSpace(toolType) ||
                                 string.Equals(toolType, "function", StringComparison.OrdinalIgnoreCase);
            if (!isFunctionType)
                continue;

            var function = tool.TryGetProperty("function", out var functionElement) &&
                           functionElement.ValueKind == JsonValueKind.Object
                ? functionElement
                : tool;
            var name = GetStringProperty(function, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                error = $"function tool at index {toolIndex} requires a non-empty name.";
                return false;
            }

            var description = GetStringProperty(function, "description") ?? string.Empty;
            var parametersJson = function.TryGetProperty("parameters", out var parameters)
                ? ElementToPayloadString(parameters)
                : """{"type":"object","properties":{}}""";
            result.Add(new ResponsesApplicationToolDeclaration(
                name.Trim(),
                description,
                parametersJson,
                ResponsesToolSchemaHashes.Compute(parametersJson)));
        }

        declaredTools = result;
        return true;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string ElementToPayloadString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
}

public static class ResponsesToolSchemaHashes
{
    public static string Compute(string parametersJson)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(parametersJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
