using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.GAgentService.Application.Responses;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed record ResponsesCreateRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input")]
    public JsonElement Input { get; init; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("tools")]
    public JsonElement Tools { get; init; }
}

internal readonly record struct ResponsesProtocolMappingResult(
    ResponsesCommandRequest? Request,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Request != null && ErrorCode == null;

    public static ResponsesProtocolMappingResult Success(ResponsesCommandRequest request) =>
        new(request, null, null);

    public static ResponsesProtocolMappingResult Failed(string code, string message) =>
        new(null, code, message);
}

internal static class ResponsesProtocolMapper
{
    // Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
    //   Old pattern: Application command contracts carried raw JsonElement from the OpenAI Responses wire protocol.
    //   New principle: Host converts boundary JSON into typed command fields before delegating orchestration to Application.
    public static ResponsesProtocolMappingResult ToCommandRequest(ResponsesCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryExtractInput(request.Input, out var prompt, out var toolResults, out var inputError))
            return ResponsesProtocolMappingResult.Failed("invalid_input", inputError);
        if (!TryExtractDeclaredTools(request.Tools, out var declaredTools, out var toolsError))
            return ResponsesProtocolMappingResult.Failed("invalid_tools", toolsError);

        return ResponsesProtocolMappingResult.Success(new ResponsesCommandRequest(
            request.Model,
            prompt,
            toolResults,
            request.Stream,
            request.PreviousResponseId,
            request.Temperature,
            request.MaxOutputTokens,
            declaredTools));
    }

    private static bool TryExtractInput(
        JsonElement input,
        out string prompt,
        out IReadOnlyList<ResponsesToolResultInput> toolResults,
        out string error)
    {
        var parts = new List<string>();
        var results = new List<ResponsesToolResultInput>();
        ExtractInput(input, parts, results);

        prompt = string.Join("\n", parts.Select(static part => part.Trim()).Where(static part => part.Length > 0));
        toolResults = results;
        if (prompt.Length > 0 || results.Count > 0)
        {
            error = string.Empty;
            return true;
        }

        error = "input must contain at least one text value.";
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

    private static bool TryExtractToolResult(JsonElement element, out ResponsesToolResultInput toolResult)
    {
        toolResult = new ResponsesToolResultInput(string.Empty, string.Empty, null);
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
        toolResult = new ResponsesToolResultInput(callId.Trim(), output ?? string.Empty, schemaHash);
        return true;
    }

    private static bool TryExtractDeclaredTools(
        JsonElement tools,
        out IReadOnlyList<ResponsesApplicationToolDeclaration> declaredTools,
        out string error)
    {
        declaredTools = [];
        error = string.Empty;
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
            // aevatar only owns function-typed tool declarations (forward / substitute /
            // additive). Entries it doesn't own are passed over so the request is still
            // admitted and the model sees the remaining tools. That covers both built-in
            // tool objects (handled below via the type check) and non-object entries that
            // some OpenAI-compatible clients emit (null padding / stray strings). Hard-
            // failing the whole request on a non-object entry would, e.g., reject a bare
            // greeting that needs no tools. A malformed *function* tool (an object with no
            // name) is still rejected below with an indexed, actionable error.
            if (tool.ValueKind != JsonValueKind.Object)
                continue;

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

internal sealed record ResponsesApiErrorResponse
{
    [JsonPropertyName("error")]
    public required ResponsesApiError Error { get; init; }
}

internal sealed record ResponsesApiError
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("param")]
    public string? Param { get; init; }
}

internal sealed record ResponsesResponseError
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }
}

internal sealed record ResponsesResponseSnapshot
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "response";

    [JsonPropertyName("created_at")]
    public required long CreatedAt { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("completed_at")]
    public long? CompletedAt { get; init; }

    [JsonPropertyName("error")]
    public ResponsesResponseError? Error { get; init; }

    [JsonPropertyName("incomplete_details")]
    public ResponsesResponseIncompleteDetails? IncompleteDetails { get; init; }

    [JsonPropertyName("input")]
    public IReadOnlyList<ResponsesInputMessage> Input { get; init; } = [];

    [JsonPropertyName("instructions")]
    public object? Instructions { get; init; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("output")]
    public IReadOnlyList<object> Output { get; init; } = [];

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool ParallelToolCalls { get; init; } = true;

    [JsonPropertyName("reasoning")]
    public ResponsesReasoningSettings Reasoning { get; init; } = new();

    [JsonPropertyName("store")]
    public bool Store { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("text")]
    public ResponsesTextSettings Text { get; init; } = new();

    [JsonPropertyName("tool_choice")]
    public string ToolChoice { get; init; } = "auto";

    [JsonPropertyName("tools")]
    public IReadOnlyList<object> Tools { get; init; } = [];

    [JsonPropertyName("top_p")]
    public double TopP { get; init; } = 1;

    [JsonPropertyName("truncation")]
    public string Truncation { get; init; } = "disabled";

    [JsonPropertyName("usage")]
    public ResponsesUsage? Usage { get; init; }

    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed record ResponsesResponseIncompleteDetails
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

internal sealed record ResponsesInputMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "message";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public IReadOnlyList<ResponsesInputTextContent> Content { get; init; } = [];
}

internal sealed record ResponsesInputTextContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "input_text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

internal sealed record ResponsesOutputMessage
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "message";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "assistant";

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("content")]
    public IReadOnlyList<ResponsesOutputTextContent> Content { get; init; } = [];
}

internal sealed record ResponsesFunctionCallOutputItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function_call";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "completed";

    [JsonPropertyName("call_id")]
    public required string CallId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; init; }
}

internal sealed record ResponsesOutputTextContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "output_text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("annotations")]
    public IReadOnlyList<object> Annotations { get; init; } = [];
}

internal sealed record ResponsesTextSettings
{
    [JsonPropertyName("format")]
    public ResponsesTextFormat Format { get; init; } = new();
}

internal sealed record ResponsesTextFormat
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";
}

internal sealed record ResponsesReasoningSettings
{
    [JsonPropertyName("effort")]
    public string? Effort { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

internal sealed record ResponsesUsage
{
    [JsonPropertyName("input_tokens")]
    public required int InputTokens { get; init; }

    [JsonPropertyName("input_tokens_details")]
    public ResponsesInputTokensDetails InputTokensDetails { get; init; } = new();

    [JsonPropertyName("output_tokens")]
    public required int OutputTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public required int TotalTokens { get; init; }

    [JsonPropertyName("output_tokens_details")]
    public ResponsesOutputTokensDetails OutputTokensDetails { get; init; } = new();
}

internal sealed record ResponsesInputTokensDetails
{
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; init; }
}

internal sealed record ResponsesOutputTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; init; }
}

/// <summary>OpenAI-compatible `/v1/models` list response.</summary>
internal sealed record ResponsesModelsListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "list";

    [JsonPropertyName("data")]
    public required IReadOnlyList<ResponsesModelEntry> Data { get; init; }
}

internal sealed record ResponsesModelEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "model";

    [JsonPropertyName("created")]
    public required long Created { get; init; }

    [JsonPropertyName("owned_by")]
    public required string OwnedBy { get; init; }

    [JsonPropertyName("group")]
    public required string Group { get; init; }

    // Reserved OpenAI-compatible extension fields. The explicit policy currently
    // carries only stable model IDs, so these remain null and are omitted from JSON.

    [JsonPropertyName("context_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextLength { get; init; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}
