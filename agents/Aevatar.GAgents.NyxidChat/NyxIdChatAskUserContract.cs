using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

internal sealed record NyxIdChatAskUserRequest(
    string Prompt,
    IReadOnlyList<NyxIdChatInputOption> Options,
    bool MultiSelect);

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
                string.IsNullOrWhiteSpace(questionElement.GetString()) ||
                !root.TryGetProperty("options", out var optionsElement) ||
                optionsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var options = new List<NyxIdChatInputOption>();
            var index = 0;
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

            if (options.Count is < MinOptions or > MaxOptions)
                return false;

            var multiSelect = root.TryGetProperty("multi_select", out var multiSelectElement) &&
                              multiSelectElement.ValueKind == JsonValueKind.True;
            request = new NyxIdChatAskUserRequest(
                questionElement.GetString()!.Trim(),
                options,
                multiSelect);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildOptionId(string requestId, int index, string label)
    {
        var source = $"{requestId.Length}:{requestId}{index}:{label.Length}:{label}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"option-{Convert.ToHexStringLower(hash)[..16]}";
    }
}
