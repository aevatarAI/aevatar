using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.Workflow.Abstractions.Security;

public static class WorkflowAuditTextSanitizer
{
    public const string RedactedValue = "[redacted]";

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
        @"(?im)^(?<prefix>[A-Za-z0-9-]*(?:authorization|token|secret|signature|api-key|apikey)[A-Za-z0-9-]*\s*:\s*).+$",
        RegexOptions.CultureInvariant);

    private static readonly Regex AssignmentSecretPattern = new(
        @"(?<prefix>\b(?:authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|secret|password|passwd|pwd|credential|signature|hmac[_-]?secret|client[_-]?secret)\b\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^,\s;}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PrivateKeyPattern = new(
        @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            : Sanitize(value);
    }

    public static string SanitizeForDisplay(string? value, int maxLength)
    {
        var sanitized = Sanitize(value);
        if (maxLength <= 0 || sanitized.Length <= maxLength)
            return maxLength <= 0 ? string.Empty : sanitized;

        return sanitized[..maxLength] + "...";
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
        return sanitized;
    }

    private static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var normalized = NormalizeKey(key);
        return normalized is
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
            "database64" or
            "rawbody" or
            "rawpayload" or
            "requestbody" or
            "responsebody" or
            "payloadsnippet";
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
}
