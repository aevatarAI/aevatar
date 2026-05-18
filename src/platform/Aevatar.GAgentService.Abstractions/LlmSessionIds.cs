using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Abstractions;

public static class LlmSessionIds
{
    public const int MaxActorIdLength = 512;

    private const string ActorPrefix = "response-sessions";
    private const string ResponseSegmentPrefix = "/response:";
    private const string TruncationMarkerPrefix = "~";
    private const int TruncationHashHexLength = 16;

    public static string BuildKey(string responseId)
    {
        if (string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId is required.", nameof(responseId));

        return responseId.Trim();
    }

    public static string BuildActorId(string responseId)
    {
        var normalized = BuildKey(responseId);
        var encoded = PercentEncode(normalized);
        var candidate = ActorPrefix + ResponseSegmentPrefix + encoded;
        if (candidate.Length <= MaxActorIdLength)
            return candidate;

        var hashTail = TruncationMarkerPrefix + BuildShortHashTail(normalized);
        var segmentBudget = MaxActorIdLength
                            - ActorPrefix.Length
                            - ResponseSegmentPrefix.Length
                            - hashTail.Length;
        if (segmentBudget < 0)
            throw new InvalidOperationException("LLM session actor id length cap is smaller than the fixed id scheme.");

        var truncated = TruncateEncodedSegment(encoded, segmentBudget) + hashTail;
        return ActorPrefix + ResponseSegmentPrefix + truncated;
    }

    [Obsolete("Use BuildActorId(responseId) so the actor address carries the stable response identity.")]
    public static string NewActorId() => BuildActorId(Guid.NewGuid().ToString("N"));

    private static string BuildShortHashTail(string responseId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(responseId));
        return Convert.ToHexString(hash).ToLowerInvariant()[..TruncationHashHexLength];
    }

    private static string TruncateEncodedSegment(string value, int maxLength)
    {
        if (maxLength <= 0)
            return string.Empty;
        if (value.Length <= maxLength)
            return value;

        var length = maxLength;
        while (length > 0 && IsInsidePercentTriplet(value, length))
            length--;
        return value[..length];
    }

    private static bool IsInsidePercentTriplet(string value, int splitIndex)
    {
        var percentIndex = value.LastIndexOf('%', Math.Max(0, splitIndex - 1), splitIndex);
        return percentIndex >= 0 && splitIndex - percentIndex < 3;
    }

    private static string PercentEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var encoded = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (IsUnescapedByte(b))
            {
                encoded.Append((char)b);
                continue;
            }

            encoded.Append('%');
            encoded.Append(b.ToString("X2"));
        }

        return encoded.ToString();
    }

    private static bool IsUnescapedByte(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'-'
            or (byte)'_'
            or (byte)'.';
}
