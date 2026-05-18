using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Abstractions;

public sealed class ResponsesAgentToolStateIdOptions
{
    public const string SectionName = "FeatureFlags";

    public bool AevatarResponsesAgentToolReadableIds { get; set; }
}

public static class ResponseAgentToolStateIds
{
    public const int MaxActorIdLength = 512;

    private const string LegacyPrefix = "responses-agent-tools-";
    private const string ReadablePrefix = "responses-agent-tools";
    private const string ScopeSegmentPrefix = "/scope:";
    private const string OwnerSegmentPrefix = "/owner:";
    private const string TruncationMarkerPrefix = "~";
    private const int TruncationHashHexLength = 16;

    /// <summary>
    /// Build a deterministic actor id from <paramref name="scopeId"/> and <paramref name="ownerSubject"/>.
    ///
    /// With <paramref name="readableIdsEnabled"/> set to <c>false</c>, this returns the legacy
    /// <c>responses-agent-tools-{sha256-prefix}</c> id exactly as before. With the flag set to
    /// <c>true</c>, it returns <c>responses-agent-tools/scope:{scope}/owner:{owner}</c> using
    /// RFC 3986 percent-encoded scope and owner segments, capped at 512 characters with a stable
    /// SHA-256 tail when truncation is required.
    ///
    /// During the 30-day rollout window, callers that read by actor id must try the readable id
    /// first and fall back to <see cref="BuildLegacyActorId"/>. The legacy hash path will be
    /// removed after that dual-read window. ADR: <c>docs/adr/0024-responses-agent-tool-actor-id-scheme.md</c>.
    /// </summary>
    public static string BuildActorId(
        string scopeId,
        string ownerSubject,
        bool readableIdsEnabled = false) =>
        readableIdsEnabled
            ? BuildReadableActorId(scopeId, ownerSubject)
            : BuildLegacyActorId(scopeId, ownerSubject);

    public static string BuildActorId(
        string scopeId,
        string ownerSubject,
        ResponsesAgentToolStateIdOptions? options) =>
        BuildActorId(scopeId, ownerSubject, options?.AevatarResponsesAgentToolReadableIds ?? false);

    public static string BuildLegacyActorId(string scopeId, string ownerSubject)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("ownerSubject is required.", nameof(ownerSubject));

        var input = $"{scopeId.Trim()}\n{ownerSubject.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return LegacyPrefix + Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    public static string BuildReadableActorId(string scopeId, string ownerSubject)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("ownerSubject is required.", nameof(ownerSubject));

        var encodedScope = PercentEncode(scopeId);
        var encodedOwner = PercentEncode(ownerSubject);
        var candidate = ComposeReadableActorId(encodedScope, encodedOwner);
        if (candidate.Length <= MaxActorIdLength)
            return candidate;

        return BuildTruncatedReadableActorId(scopeId, ownerSubject, encodedScope, encodedOwner);
    }

    public static bool TryDecodeReadableActorId(
        string actorId,
        out string scopeId,
        out string ownerSubject)
    {
        scopeId = string.Empty;
        ownerSubject = string.Empty;

        if (string.IsNullOrWhiteSpace(actorId) ||
            !actorId.StartsWith(ReadablePrefix + ScopeSegmentPrefix, StringComparison.Ordinal))
            return false;

        var ownerPrefixIndex = actorId.IndexOf(OwnerSegmentPrefix, ReadablePrefix.Length + ScopeSegmentPrefix.Length, StringComparison.Ordinal);
        if (ownerPrefixIndex < 0)
            return false;

        var encodedScope = actorId[(ReadablePrefix.Length + ScopeSegmentPrefix.Length)..ownerPrefixIndex];
        var encodedOwner = actorId[(ownerPrefixIndex + OwnerSegmentPrefix.Length)..];
        if (!TryPercentDecode(encodedScope, out scopeId) ||
            !TryPercentDecode(encodedOwner, out ownerSubject))
        {
            scopeId = string.Empty;
            ownerSubject = string.Empty;
            return false;
        }

        return true;
    }

    public static string NewTaskId() => "task_" + Guid.NewGuid().ToString("N");

    public static string NewWebTraceId() => "web_" + Guid.NewGuid().ToString("N");

    private static string ComposeReadableActorId(string encodedScope, string encodedOwner) =>
        ReadablePrefix + ScopeSegmentPrefix + encodedScope + OwnerSegmentPrefix + encodedOwner;

    private static string BuildTruncatedReadableActorId(
        string scopeId,
        string ownerSubject,
        string encodedScope,
        string encodedOwner)
    {
        var hashTail = TruncationMarkerPrefix + BuildShortHashTail(scopeId, ownerSubject);
        var segmentBudget = MaxActorIdLength
                            - ReadablePrefix.Length
                            - ScopeSegmentPrefix.Length
                            - OwnerSegmentPrefix.Length
                            - hashTail.Length;

        if (segmentBudget < 0)
            throw new InvalidOperationException("Responses agent tool actor id length cap is smaller than the fixed id scheme.");

        var scopeLimit = encodedScope.Length;
        var ownerLimit = encodedOwner.Length;
        var appendTailToScope = encodedScope.Length >= encodedOwner.Length;
        if (appendTailToScope)
        {
            if (encodedOwner.Length <= segmentBudget)
            {
                ownerLimit = encodedOwner.Length;
                scopeLimit = segmentBudget - ownerLimit;
            }
            else
            {
                ownerLimit = segmentBudget / 2;
                scopeLimit = segmentBudget - ownerLimit;
            }
        }
        else
        {
            if (encodedScope.Length <= segmentBudget)
            {
                scopeLimit = encodedScope.Length;
                ownerLimit = segmentBudget - scopeLimit;
            }
            else
            {
                scopeLimit = segmentBudget / 2;
                ownerLimit = segmentBudget - scopeLimit;
            }
        }

        var truncatedScope = TruncateEncodedSegment(encodedScope, scopeLimit);
        var truncatedOwner = TruncateEncodedSegment(encodedOwner, ownerLimit);
        if (appendTailToScope)
            truncatedScope += hashTail;
        else
            truncatedOwner += hashTail;

        return ComposeReadableActorId(truncatedScope, truncatedOwner);
    }

    private static string BuildShortHashTail(string scopeId, string ownerSubject)
    {
        var input = $"{scopeId}|{ownerSubject}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
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

    private static bool TryPercentDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '%')
            {
                if (ch > 0x7F)
                    return false;
                bytes.Add((byte)ch);
                continue;
            }

            if (i + 2 >= value.Length ||
                !TryParseHexByte(value[i + 1], value[i + 2], out var b))
            {
                return false;
            }

            bytes.Add(b);
            i += 2;
        }

        decoded = Encoding.UTF8.GetString(bytes.ToArray());
        return true;
    }

    private static bool IsUnescapedByte(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is (byte)'-' or (byte)'_' or (byte)'.';

    private static bool TryParseHexByte(char high, char low, out byte value)
    {
        value = 0;
        if (!TryParseHexNibble(high, out var highNibble) ||
            !TryParseHexNibble(low, out var lowNibble))
            return false;

        value = (byte)((highNibble << 4) | lowNibble);
        return true;
    }

    private static bool TryParseHexNibble(char ch, out int value)
    {
        value = ch switch
        {
            >= '0' and <= '9' => ch - '0',
            >= 'A' and <= 'F' => ch - 'A' + 10,
            >= 'a' and <= 'f' => ch - 'a' + 10,
            _ => -1,
        };
        return value >= 0;
    }
}
