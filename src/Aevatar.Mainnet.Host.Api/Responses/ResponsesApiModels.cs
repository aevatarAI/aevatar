using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

internal sealed record NormalizedResponsesRequest(
    string ResponseId,
    string MessageItemId,
    string Model,
    string Prompt,
    bool Stream,
    string? PreviousResponseId,
    double? Temperature,
    int? MaxOutputTokens,
    IReadOnlyList<ResponsesToolDeclaration> DeclaredTools,
    IReadOnlyList<ResponsesToolResultInput> ToolResults);

internal sealed record ResponsesToolDeclaration(
    string Name,
    string Description,
    string ParametersJson,
    string SchemaHash);

internal sealed record ResponsesToolResultInput(
    string CallId,
    string Output,
    string? SchemaHash);

internal readonly record struct ResponsesRequestNormalizationResult(
    NormalizedResponsesRequest? Request,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Request != null && ErrorCode == null;

    public static ResponsesRequestNormalizationResult Success(NormalizedResponsesRequest request) =>
        new(request, null, null);

    public static ResponsesRequestNormalizationResult Failed(string code, string message) =>
        new(null, code, message);
}

internal static class ResponsesRequestNormalizer
{
    public static ResponsesRequestNormalizationResult Normalize(ResponsesCreateRequest request)
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

        return ResponsesRequestNormalizationResult.Success(new NormalizedResponsesRequest(
            ResponseId: ResponsesIds.NewResponseId(),
            MessageItemId: ResponsesIds.NewMessageId(),
            Model: model,
            Prompt: prompt,
            Stream: request.Stream == true,
            PreviousResponseId: NormalizeOptional(request.PreviousResponseId),
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
        out IReadOnlyList<ResponsesToolDeclaration> declaredTools,
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

        var result = new List<ResponsesToolDeclaration>();
        var toolIndex = -1;
        foreach (var tool in tools.EnumerateArray())
        {
            toolIndex++;
            if (tool.ValueKind != JsonValueKind.Object)
            {
                error = $"tool at index {toolIndex} must be an object.";
                return false;
            }

            // OpenAI Responses API allows built-in tool declarations like
            // `{type: "web_search_preview"}` / `{type: "file_search", ...}` /
            // `{type: "code_interpreter", ...}` / `{type: "computer_use_preview", ...}`
            // that don't carry a `function` block or `name`. They're routing hints to
            // the model provider, not custom function definitions. aevatar's classifier
            // only owns function-typed tools (forward / substitute / additive); silently
            // pass over the rest so an OpenAI-compatible client (CC Switch, Codex,
            // Cursor) can advertise built-ins without breaking the request — even
            // though aevatar won't map them to a local handler and the model provider
            // gets to decide what to do with them.
            // OpenAI Responses API allows built-in tool declarations like
            // `{type: "web_search_preview"}` / `{type: "file_search", ...}` /
            // `{type: "code_interpreter", ...}` / `{type: "computer_use_preview", ...}`
            // that don't carry a `function` block or `name`. They're routing hints to
            // the model provider, not custom function definitions. aevatar's classifier
            // only owns function-typed tools (forward / substitute / additive); silently
            // pass over the rest so an OpenAI-compatible client (CC Switch, Codex,
            // Cursor) can advertise built-ins without breaking the request — even
            // though aevatar won't map them to a local handler and the model provider
            // gets to decide what to do with them.
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
            result.Add(new ResponsesToolDeclaration(
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

internal static class ResponsesToolSchemaHashes
{
    public static string Compute(string parametersJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(parametersJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
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

/// <summary>OpenAI-spec `/v1/models` list response. Extension fields (route_value, group, status)
/// surface aevatar's multi-route topology to aevatar-aware clients without breaking OpenAI-spec
/// consumers (Codex / Cursor read only `id` / `object` / `created` / `owned_by`).</summary>
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

    [JsonPropertyName("route_value")]
    public required string RouteValue { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    // Optional upstream-forwarded metadata. Present when the upstream `/v1/models`
    // response carried richer fields (anthropic gives display_name + max_input_tokens
    // + max_tokens; OpenAI-spec backends like deepseek / chrono-llm only return the
    // minimal `{id, object, created, owned_by}` shape so these stay null and are
    // omitted from JSON output). OpenRouter-style clients (CC Switch, Codex) read
    // these to size requests; sparse upstreams cause a UI warning but not failure.

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

/// <summary>Splits an OpenRouter-style `vendor/model` identifier. Vendor is preserved only when it
/// looks like a NyxID service slug (lowercase + digits + hyphens, length 2-64); otherwise the whole
/// string is treated as a bare model name (e.g. for Anthropic-style namespacing the LLM provider
/// may want intact).
/// Gateway-routed models are emitted bare by the catalog, so a prefix is only ever produced for
/// UserService / ProxyService routes that resolve to `/api/v1/proxy/s/{slug}`. A client that
/// invents an unknown slug gets a clean NyxID 404 — fail-closed; we don't validate against the
/// catalog here to keep `/v1/responses` off the catalog HTTP critical path.</summary>
internal static class ResponsesModelRouteParser
{
    public static ResponsesModelRoute Parse(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var trimmed = model.Trim();
        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= trimmed.Length - 1)
            return new ResponsesModelRoute(null, trimmed);

        var prefix = trimmed[..slashIndex];
        var rest = trimmed[(slashIndex + 1)..];
        return LooksLikeSlug(prefix)
            ? new ResponsesModelRoute(prefix, rest)
            : new ResponsesModelRoute(null, trimmed);
    }

    private static bool LooksLikeSlug(string value)
    {
        if (value.Length is < 2 or > 64) return false;
        if (!char.IsAsciiLetterLower(value[0])) return false;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                return false;
        }
        return true;
    }
}

internal readonly record struct ResponsesModelRoute(string? RouteSlug, string Model);

internal static class ResponsesIds
{
    public static string NewResponseId() => "resp_" + NewOpaqueId();

    public static string NewMessageId() => "msg_" + NewOpaqueId();

    public static string NewOpaqueId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
