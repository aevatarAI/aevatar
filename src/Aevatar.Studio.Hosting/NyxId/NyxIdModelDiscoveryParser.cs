using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Hosting.NyxId;

internal static class NyxIdModelDiscoveryParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };

    internal static NyxIdDiscoveredModels Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ModelsResponse response;
        try
        {
            response = JsonSerializer.Deserialize<ModelsResponse>(json, SerializerOptions)
                ?? throw Invalid("response body must be an object");
        }
        catch (JsonException ex)
        {
            throw new NyxIdModelDiscoveryException(
                NyxIdModelDiscoveryFailureKind.ResponseInvalid,
                statusCode: null,
                "NyxID upstream models response is invalid JSON.",
                ex);
        }

        if (response.Data is null)
            throw Invalid("data is required");
        if (response.Data.Count > LLMSelectionPolicy.MaxModelsPerCatalog)
        {
            throw new NyxIdModelDiscoveryException(
                NyxIdModelDiscoveryFailureKind.ResponseTooLarge,
                statusCode: null,
                $"NyxID upstream models response exceeded {LLMSelectionPolicy.MaxModelsPerCatalog} entries.");
        }

        var modelIds = new SortedSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < response.Data.Count; index++)
        {
            var model = response.Data[index]
                ?? throw Invalid($"data[{index}] must be an object");
            var modelId = model.Id;
            if (!IsCanonicalModelId(modelId))
                throw Invalid($"data[{index}].id is not a canonical model ID");
            modelIds.Add(modelId!);
        }

        var defaultModelId = ParseDefaultModel(response.DefaultModel, modelIds);
        return new NyxIdDiscoveredModels(modelIds.ToArray(), defaultModelId);
    }

    private static string? ParseDefaultModel(JsonElement value, IReadOnlySet<string> modelIds)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || !IsCanonicalModelId(value.GetString()))
            throw Invalid("default_model must be a canonical model ID or null");

        var modelId = value.GetString()!;
        if (!modelIds.Contains(modelId))
            throw Invalid("default_model must be present in data");
        return modelId;
    }

    private static bool IsCanonicalModelId(string? modelId) =>
        !string.IsNullOrEmpty(modelId) &&
        string.Equals(modelId, modelId.Trim(), StringComparison.Ordinal) &&
        !modelId.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(modelId) <= LLMSelectionPolicy.MaxModelIdUtf8Bytes &&
        modelId.IndexOfAny(['*', '?', '[', ']', '{', '}']) < 0;

    private static NyxIdModelDiscoveryException Invalid(string detail) =>
        new(
            NyxIdModelDiscoveryFailureKind.ResponseInvalid,
            statusCode: null,
            $"NyxID upstream models response is invalid: {detail}.");

    private sealed class ModelsResponse
    {
        [JsonPropertyName("data")]
        public IReadOnlyList<ModelEntry?>? Data { get; init; }

        [JsonPropertyName("default_model")]
        public JsonElement DefaultModel { get; init; }
    }

    private sealed class ModelEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }
}
