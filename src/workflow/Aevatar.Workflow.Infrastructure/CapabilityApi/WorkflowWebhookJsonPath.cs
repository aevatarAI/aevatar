using System.Text;
using System.Text.Json;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class WorkflowWebhookJsonPath
{
    public static bool IsValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Encoding.UTF8.GetByteCount(path.Trim()) > WorkflowWebhookIngressLimits.MaxJsonPathBytes)
        {
            return false;
        }

        return TryNormalize(path, out _);
    }

    public static bool TryExtractScalar(JsonElement root, string path, out string value)
    {
        value = string.Empty;
        if (!TryNormalize(path, out var segments))
            return false;

        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        switch (current.ValueKind)
        {
            case JsonValueKind.String:
                value = current.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = current.GetRawText();
                return true;
            default:
                return false;
        }
    }

    private static bool TryNormalize(string path, out string[] segments)
    {
        segments = [];
        var normalized = path.Trim();
        if (normalized.StartsWith("$.", StringComparison.Ordinal))
            normalized = normalized[2..];
        else if (normalized.StartsWith(".", StringComparison.Ordinal))
            normalized = normalized[1..];

        if (normalized.Length == 0 ||
            normalized.Contains('[', StringComparison.Ordinal) ||
            normalized.Contains(']', StringComparison.Ordinal))
        {
            return false;
        }

        segments = normalized.Split('.', StringSplitOptions.TrimEntries);
        return segments.Length is > 0 and <= WorkflowWebhookIngressLimits.MaxJsonPathSegments &&
               segments.All(static segment => segment.Length > 0);
    }
}
