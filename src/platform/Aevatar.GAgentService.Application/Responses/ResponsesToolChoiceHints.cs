using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.LLMProviders;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

public static class ResponsesToolChoiceHints
{
    public static ResponsesToolChoiceHintPlan Create(string? toolName, Struct? prefilledArguments)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return ResponsesToolChoiceHintPlan.Empty;

        var prefilled = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (prefilledArguments != null)
        {
            foreach (var (key, value) in prefilledArguments.Fields)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                prefilled[key.Trim()] = ToJsonNode(value);
            }
        }

        return new ResponsesToolChoiceHintPlan(toolName.Trim(), prefilled);
    }

    private static JsonNode? ToJsonNode(Value value)
    {
        var json = JsonFormatter.Default.Format(value);
        return JsonNode.Parse(json);
    }
}

public sealed class ResponsesToolChoiceHintPlan
{
    internal ResponsesToolChoiceHintPlan(
        string toolName,
        IReadOnlyDictionary<string, JsonNode?> prefilledArguments)
    {
        ToolName = toolName;
        PrefilledArguments = prefilledArguments;
    }

    public static ResponsesToolChoiceHintPlan Empty { get; } = new(
        string.Empty,
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal));

    public string ToolName { get; }

    public IReadOnlyDictionary<string, JsonNode?> PrefilledArguments { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(ToolName);

    public string PrefilledArgumentsJson() =>
        new JsonObject(PrefilledArguments
                .Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))
                .ToArray())
            .ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    // Refactor (iter355/issue1438-first):
    //   Old pattern: durable LlmSession tool choice hint writes used only prefilled JSON strings.
    //   New principle: command builders write typed Struct and keep JSON only as legacy fallback.
    public Struct PrefilledArgumentsStruct() =>
        string.IsNullOrWhiteSpace(PrefilledArgumentsJson())
            ? new Struct()
            : ResponsesProtoPayloads.ParseStruct(PrefilledArgumentsJson());

    public ToolCall Apply(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (IsEmpty)
            return call;

        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal))
            return new ToolCall
            {
                Id = call.Id,
                Name = call.Name,
                ArgumentsJson = BuildStructuredError(
                    "tool_choice_hint_mismatch",
                    $"Tool choice hint requires '{ToolName}', but the model called '{call.Name}'.",
                    "tool_name"),
            };

        var merged = new JsonObject();
        foreach (var (key, value) in PrefilledArguments)
            merged[key] = value?.DeepClone();

        JsonObject modelArguments;
        try
        {
            modelArguments = ParseArguments(call.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            return CloneWithArguments(
                call,
                BuildStructuredError(
                    "invalid_tool_arguments",
                    $"Arguments must be a JSON object: {ex.Message}",
                    "arguments"));
        }

        foreach (var (key, value) in modelArguments)
        {
            if (PrefilledArguments.TryGetValue(key, out var prefilled) &&
                !JsonNode.DeepEquals(prefilled, value))
            {
                return CloneWithArguments(
                    call,
                    BuildStructuredError(
                        "tool_choice_prefill_conflict",
                        $"Tool argument '{key}' conflicts with server-trusted prefilled_arguments.",
                        key));
            }

            if (!PrefilledArguments.ContainsKey(key))
                merged[key] = value?.DeepClone();
        }

        return CloneWithArguments(
            call,
            merged.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    public static bool IsStructuredErrorArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static JsonObject ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new JsonObject();

        var node = JsonNode.Parse(argumentsJson);
        return node as JsonObject
               ?? throw new JsonException("Root value must be an object.");
    }

    private static ToolCall CloneWithArguments(ToolCall call, string argumentsJson) =>
        new()
        {
            Id = call.Id,
            Name = call.Name,
            ArgumentsJson = argumentsJson,
        };

    private static string BuildStructuredError(
        string code,
        string message,
        string field) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code,
                message,
                field,
            },
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
}
