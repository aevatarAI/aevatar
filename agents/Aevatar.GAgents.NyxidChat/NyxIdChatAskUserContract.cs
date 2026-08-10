using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

internal sealed record NyxIdChatAskUserRequest(
    string Prompt,
    IReadOnlyList<NyxIdChatInputOption> Options,
    bool AllowFreeText,
    bool MultiSelect,
    NyxIdChatNumericThresholdInputSpec? NumericThreshold);

internal static class NyxIdChatAskUserContract
{
    internal const string ToolName = "ask_user";
    private const int MinOptions = 2;
    private const int MaxOptions = 6;

    internal static bool IsAskUser(NyxIdChatToolCall? call) =>
        call is not null &&
        string.Equals(call.ToolName?.Trim(), ToolName, StringComparison.Ordinal);

    internal static bool TryParse(
        string? requestId,
        string? argumentsJson,
        out NyxIdChatAskUserRequest request)
    {
        request = null!;
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("question", out var questionElement) ||
                questionElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(questionElement.GetString()))
            {
                return false;
            }

            var options = new List<NyxIdChatInputOption>();
            if (root.TryGetProperty("options", out var optionsElement) &&
                optionsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var index = 0;
            if (optionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var optionElement in optionsElement.EnumerateArray())
                {
                    if (optionElement.ValueKind != JsonValueKind.Object ||
                        !optionElement.TryGetProperty("label", out var labelElement) ||
                        labelElement.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(labelElement.GetString()))
                    {
                        return false;
                    }

                    var label = labelElement.GetString()!.Trim();
                    var description = optionElement.TryGetProperty("description", out var descriptionElement) &&
                                      descriptionElement.ValueKind == JsonValueKind.String
                        ? descriptionElement.GetString()?.Trim() ?? string.Empty
                        : string.Empty;
                    options.Add(new NyxIdChatInputOption
                    {
                        OptionId = BuildOptionId(requestId.Trim(), index, label),
                        Label = label,
                        Description = description,
                    });
                    index++;
                }
            }

            var allowFreeText = root.TryGetProperty("allow_free_text", out var allowFreeTextElement) &&
                                allowFreeTextElement.ValueKind == JsonValueKind.True;
            var multiSelect = root.TryGetProperty("multi_select", out var multiSelectElement) &&
                              multiSelectElement.ValueKind == JsonValueKind.True;
            if (!TryParseNumericThreshold(root, out var numericThreshold))
                return false;
            if (numericThreshold is not null &&
                (!allowFreeText || multiSelect || options.Count != 0))
            {
                return false;
            }
            if (options.Count == 0)
            {
                if (!allowFreeText || multiSelect)
                    return false;
            }
            else if (options.Count is < MinOptions or > MaxOptions)
            {
                return false;
            }

            request = new NyxIdChatAskUserRequest(
                questionElement.GetString()!.Trim(),
                options,
                allowFreeText,
                multiSelect,
                numericThreshold);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseNumericThreshold(
        JsonElement root,
        out NyxIdChatNumericThresholdInputSpec? spec)
    {
        spec = null;
        if (!root.TryGetProperty("numeric_threshold", out var threshold))
            return true;
        if (threshold.ValueKind != JsonValueKind.Object ||
            !threshold.TryGetProperty("suggested_value", out var suggested) ||
            !threshold.TryGetProperty("minimum_value", out var minimum) ||
            !threshold.TryGetProperty("maximum_value", out var maximum) ||
            suggested.ValueKind != JsonValueKind.Number ||
            minimum.ValueKind != JsonValueKind.Number ||
            maximum.ValueKind != JsonValueKind.Number ||
            !suggested.TryGetInt64(out var suggestedValue) ||
            !minimum.TryGetInt64(out var minimumValue) ||
            !maximum.TryGetInt64(out var maximumValue) ||
            minimumValue > maximumValue ||
            suggestedValue < minimumValue ||
            suggestedValue > maximumValue)
        {
            return false;
        }

        spec = new NyxIdChatNumericThresholdInputSpec
        {
            SuggestedValue = suggestedValue,
            MinimumValue = minimumValue,
            MaximumValue = maximumValue,
        };
        return true;
    }

    private static string BuildOptionId(string requestId, int index, string label)
    {
        var source = $"{requestId.Length}:{requestId}{index}:{label.Length}:{label}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"option-{Convert.ToHexStringLower(hash)[..16]}";
    }
}
