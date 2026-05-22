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
