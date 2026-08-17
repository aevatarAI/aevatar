using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions.Helpers;

namespace Aevatar.Workflow.Abstractions.Security;

public static class WorkflowAuditTextSanitizer
{
    public const string RedactedValue = "[redacted]";
    public const string HeadTailTruncationMarker = "\n...[truncated]...\n";
    public const int MaxDiagnosticEvidenceUtf8Bytes = 64 * 1024;

    private static readonly Regex BearerTokenPattern = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BasicCredentialPattern = new(
        @"\bBasic\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex HmacSignaturePattern = new(
        @"\bsha256=[a-f0-9]{16,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UrlSecretQueryPattern = new(
        @"(?<prefix>[?&](?:api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|secret|password|signature)=)[^&#\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HeaderSecretPattern = new(
        @"(?im)^(?<prefix>[ \t]*[A-Za-z0-9_-]*(?:authorization|cookie|token|api[_-]?key|secret|password|passwd|pwd|credential|signature|private[_-]?key|signing[_-]?key)[A-Za-z0-9_-]*\s*:\s*).+$",
        RegexOptions.CultureInvariant);

    private static readonly Regex AssignmentSecretPattern = new(
        @"(?<prefix>\b[A-Za-z0-9_-]*(?:authorization|cookie|token|api[_-]?key|secret|password|passwd|pwd|credential|signature|private[_-]?key|signing[_-]?key)\b\s*[:=]\s*)(?!\[redacted\])(?:""[^""]*""|'[^']*'|[^,\s;}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EmailAddressPattern = new(
        @"(?<![A-Za-z0-9.!#$%&'*+/=?^_`{|}~-])[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+(?![A-Za-z0-9-])",
        RegexOptions.CultureInvariant);

    private static readonly Regex PrivateKeyPattern = new(
        @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] SensitiveKeySuffixes =
    [
        "authorization",
        "cookie",
        "token",
        "apikey",
        "secret",
        "password",
        "passwd",
        "pwd",
        "credential",
        "signature",
        "privatekey",
        "signingkey",
    ];

    private static readonly HashSet<string> SensitiveKeyWords = new(StringComparer.Ordinal)
    {
        "authorization",
        "cookie",
        "token",
        "secret",
        "password",
        "passwd",
        "pwd",
        "credential",
        "signature",
    };

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var text = value.Trim();
        if (TrySanitizeJson(text, out var sanitizedJson))
            return sanitizedJson;

        return SanitizeScalar(value);
    }

    public static string SanitizeValue(string? fieldName, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return IsSensitiveKey(fieldName)
            ? RedactedValue
            : SanitizeForStorage(value);
    }

    public static string SanitizeForDisplay(string? value, int maxLength)
    {
        var sanitized = SanitizeForStorage(value);
        if (maxLength <= 0 || sanitized.Length <= maxLength)
            return maxLength <= 0 ? string.Empty : sanitized;

        var retainedLength = maxLength;
        if (char.IsHighSurrogate(sanitized[retainedLength - 1]) &&
            char.IsLowSurrogate(sanitized[retainedLength]))
        {
            retainedLength--;
        }

        return sanitized[..retainedLength] + "...";
    }

    /// <summary>
    /// Scrubs persistence-bound text before retaining a UTF-8 byte-bounded head and tail.
    /// The returned value is always valid UTF-16 and never exceeds <paramref name="maxUtf8Bytes"/>
    /// when encoded as UTF-8.
    /// </summary>
    public static string SanitizeForStorage(string? value, int maxUtf8Bytes, out bool truncated)
    {
        var sanitized = SanitizeForStorage(value);
        return TruncateUtf8HeadTail(sanitized, maxUtf8Bytes, out truncated);
    }

    public static string SanitizeForStorage(string? value)
    {
        var scrubbed = SecretScrubber.ScrubJson(NormalizeInvalidUtf16(value));
        return Sanitize(scrubbed)
            .Replace(SecretScrubber.Marker, RedactedValue, StringComparison.Ordinal);
    }

    public static Dictionary<string, string> SanitizeMap(IEnumerable<KeyValuePair<string, string>>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source == null)
            return result;

        foreach (var (key, value) in source)
        {
            var sanitizedKey = Sanitize(key);
            result[sanitizedKey] = SanitizeValue(key, value);
        }

        return result;
    }

    private static bool TrySanitizeJson(string text, out string sanitizedJson)
    {
        sanitizedJson = string.Empty;
        if (text.Length == 0 || text[0] is not ('{' or '[' or '"'))
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteSanitizedElement(document.RootElement, writer, null);
            }

            sanitizedJson = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteSanitizedElement(JsonElement element, Utf8JsonWriter writer, string? fieldName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveKey(property.Name))
                        writer.WriteStringValue(RedactedValue);
                    else
                        WriteSanitizedElement(property.Value, writer, property.Name);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteSanitizedElement(item, writer, fieldName);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(IsSensitiveKey(fieldName)
                    ? RedactedValue
                    : SanitizeScalar(element.GetString()));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string SanitizeScalar(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sanitized = PrivateKeyPattern.Replace(value, RedactedValue);
        sanitized = BearerTokenPattern.Replace(sanitized, "Bearer " + RedactedValue);
        sanitized = BasicCredentialPattern.Replace(sanitized, "Basic " + RedactedValue);
        sanitized = JwtPattern.Replace(sanitized, RedactedValue);
        sanitized = HmacSignaturePattern.Replace(sanitized, "sha256=" + RedactedValue);
        sanitized = UrlSecretQueryPattern.Replace(sanitized, match => match.Groups["prefix"].Value + RedactedValue);
        sanitized = HeaderSecretPattern.Replace(sanitized, match => match.Groups["prefix"].Value + RedactedValue);
        sanitized = AssignmentSecretPattern.Replace(sanitized, match => match.Groups["prefix"].Value + RedactedValue);
        sanitized = EmailAddressPattern.Replace(sanitized, RedactedValue);
        return sanitized;
    }

    private static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var normalized = NormalizeKey(key);
        if (normalized is
            "authorization" or
            "cookie" or
            "setcookie" or
            "bearer" or
            "bearertoken" or
            "token" or
            "accesstoken" or
            "refreshtoken" or
            "idtoken" or
            "nyxidaccesstoken" or
            "sendernyxidaccesstoken" or
            "apikey" or
            "secret" or
            "clientsecret" or
            "hmacsecret" or
            "password" or
            "passwd" or
            "pwd" or
            "credential" or
            "credentials" or
            "signature" or
            "privatekey" or
            "signingkey" or
            "email" or
            "emails" or
            "emailaddress" or
            "emailaddresses" or
            "userid" or
            "userids" or
            "openid" or
            "openids" or
            "unionid" or
            "unionids" or
            "externaluserid" or
            "externaluserids" or
            "mobile" or
            "mobiles" or
            "phonenumber" or
            "phonenumbers" or
            "database64" or
            "rawbody" or
            "rawpayload" or
            "requestbody" or
            "responsebody" or
            "payloadsnippet")
        {
            return true;
        }

        var words = SplitKeyWords(key);
        if (words.Any(SensitiveKeyWords.Contains))
            return true;
        for (var index = 0; index + 1 < words.Count; index++)
        {
            if ((words[index] == "api" || words[index] == "private" || words[index] == "signing") &&
                words[index + 1] == "key")
                return true;
        }

        return SensitiveKeySuffixes.Any(suffix =>
            normalized.Length > suffix.Length &&
            normalized.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> SplitKeyWords(string key)
    {
        var words = new List<string>();
        var current = new StringBuilder(key.Length);
        for (var index = 0; index < key.Length; index++)
        {
            var ch = key[index];
            if (!char.IsLetterOrDigit(ch))
            {
                FlushKeyWord(words, current);
                continue;
            }

            var startsCamelWord = current.Length > 0 && char.IsUpper(ch) &&
                                  (char.IsLower(key[index - 1]) ||
                                   (char.IsUpper(key[index - 1]) && index + 1 < key.Length &&
                                    char.IsLower(key[index + 1])));
            if (startsCamelWord)
                FlushKeyWord(words, current);
            current.Append(char.ToLowerInvariant(ch));
        }

        FlushKeyWord(words, current);
        return words;
    }

    private static void FlushKeyWord(ICollection<string> words, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        words.Add(current.ToString());
        current.Clear();
    }

    private static string NormalizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static string NormalizeInvalidUtf16(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        StringBuilder? normalized = null;
        var index = 0;
        while (index < value.Length)
        {
            var status = Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var charsConsumed);
            if (status == OperationStatus.Done)
            {
                if (normalized != null)
                    AppendRune(normalized, rune);
                index += charsConsumed;
                continue;
            }

            normalized ??= new StringBuilder(value.Length).Append(value.AsSpan(0, index));
            AppendRune(normalized, Rune.ReplacementChar);
            index++;
        }

        return normalized?.ToString() ?? value;
    }

    private static string TruncateUtf8HeadTail(string value, int maxUtf8Bytes, out bool truncated)
    {
        var valueByteCount = Encoding.UTF8.GetByteCount(value);
        truncated = valueByteCount > Math.Max(0, maxUtf8Bytes);
        if (!truncated)
            return value;
        if (maxUtf8Bytes <= 0)
            return string.Empty;

        var markerByteCount = Encoding.UTF8.GetByteCount(HeadTailTruncationMarker);
        if (maxUtf8Bytes <= markerByteCount)
            return TakeUtf8Prefix(HeadTailTruncationMarker, maxUtf8Bytes);

        var retainedByteBudget = maxUtf8Bytes - markerByteCount;
        var headByteBudget = (retainedByteBudget + 1) / 2;
        var tailByteBudget = retainedByteBudget - headByteBudget;
        return TakeUtf8Prefix(value, headByteBudget) +
               HeadTailTruncationMarker +
               TakeUtf8Suffix(value, tailByteBudget);
    }

    private static string TakeUtf8Prefix(string value, int maxUtf8Bytes)
    {
        var builder = new StringBuilder();
        var index = 0;
        var byteCount = 0;
        while (index < value.Length)
        {
            var status = Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                charsConsumed = 1;
            }

            if (byteCount + rune.Utf8SequenceLength > maxUtf8Bytes)
                break;

            byteCount += rune.Utf8SequenceLength;
            AppendRune(builder, rune);
            index += charsConsumed;
        }

        return builder.ToString();
    }

    private static string TakeUtf8Suffix(string value, int maxUtf8Bytes)
    {
        var start = value.Length;
        var byteCount = 0;
        var retainedRunes = new List<Rune>();
        while (start > 0)
        {
            var status = Rune.DecodeLastFromUtf16(value.AsSpan(0, start), out var rune, out var charsConsumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                charsConsumed = 1;
            }

            if (byteCount + rune.Utf8SequenceLength > maxUtf8Bytes)
                break;

            byteCount += rune.Utf8SequenceLength;
            retainedRunes.Add(rune);
            start -= charsConsumed;
        }

        var builder = new StringBuilder();
        for (var index = retainedRunes.Count - 1; index >= 0; index--)
            AppendRune(builder, retainedRunes[index]);
        return builder.ToString();
    }

    private static void AppendRune(StringBuilder builder, Rune rune)
    {
        Span<char> encoded = stackalloc char[2];
        var charsWritten = rune.EncodeToUtf16(encoded);
        builder.Append(encoded[..charsWritten]);
    }
}
