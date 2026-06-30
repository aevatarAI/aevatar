using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

internal static class LarkCardTextFallbackPolicy
{
    internal const int SegmentThreshold = 28_000;
    internal const string StatusText = "Processing your request. Please wait...";
    internal const string AccessTokenUnavailableErrorCode = "nyx_access_token_unavailable";

    internal static IReadOnlyList<string> SegmentText(string text)
    {
        var normalized = string.IsNullOrEmpty(text) ? " " : text;
        if (normalized.Length <= SegmentThreshold)
            return [normalized];

        var initialTotal = (int)Math.Ceiling((double)normalized.Length / SegmentThreshold);
        var prefixProbe = $"Result ({initialTotal}/{initialTotal})\n";
        var bodyLimit = Math.Max(1, SegmentThreshold - prefixProbe.Length);
        var total = (int)Math.Ceiling((double)normalized.Length / bodyLimit);
        var segments = new List<string>();
        var offset = 0;
        var index = 1;
        while (offset < normalized.Length)
        {
            var length = Math.Min(bodyLimit, normalized.Length - offset);
            segments.Add($"Result ({index}/{total})\n{normalized.Substring(offset, length)}");
            offset += length;
            index++;
        }

        return segments;
    }

    internal static string BuildTextContentJson(string text) =>
        JsonSerializer.Serialize(new { text });
}
