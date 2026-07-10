using Aevatar.Audit;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Core.Sanitization;

public sealed class AuditRecordSanitizer
{
    private static readonly string[] SecretKeyFragments =
    [
        "authorization",
        "bearer",
        "token",
        "secret",
        "password",
        "cookie",
        "api_key",
        "apikey",
        "full_key",
        "oauth",
        "credential",
        "private_key",
        "raw_subject",
        "sender_binding_id",
        "full_prompt",
        "tool_args",
        "tool_result",
        "raw_body",
        "headers"
    ];

    private readonly AuditRecordSanitizerOptions _options;

    public AuditRecordSanitizer(AuditRecordSanitizerOptions? options = null)
    {
        _options = options ?? new AuditRecordSanitizerOptions();
    }

    public AuditRecord Sanitize(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Validate(record);

        var sanitized = record.Clone();
        sanitized.RequestSummary = CleanText(record.RequestSummary, _options.MaxSummaryLength);
        sanitized.ResultSummary = CleanText(record.ResultSummary, _options.MaxSummaryLength);
        sanitized.ErrorCode = CleanText(record.ErrorCode, _options.MaxSummaryLength);
        sanitized.ErrorSummary = CleanText(record.ErrorSummary, _options.MaxSummaryLength);

        sanitized.Annotations.Clear();
        foreach (var annotation in record.Annotations.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (sanitized.Annotations.Count >= _options.MaxAnnotations)
            {
                break;
            }

            var key = CleanText(annotation.Key, _options.MaxAnnotationKeyLength);
            RejectSecretCarrier(key, annotation.Value);
            sanitized.Annotations.Add(key, CleanText(annotation.Value, _options.MaxAnnotationValueLength));
        }

        return sanitized;
    }

    private static void Validate(AuditRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.AuditId))
        {
            throw new ArgumentException("AuditId is required.", nameof(record));
        }

        if (record.OccurredAt is null || record.OccurredAt == Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch))
        {
            throw new ArgumentException("OccurredAt is required.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.ScopeId))
        {
            throw new ArgumentException("ScopeId is required.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.AuditActorId))
        {
            throw new ArgumentException("AuditActorId is required.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.IdentityKeyId))
        {
            throw new ArgumentException("IdentityKeyId is required.", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.OperationName))
        {
            throw new ArgumentException("OperationName is required.", nameof(record));
        }

        if (record.ActorKind == AuditActorKind.Unspecified ||
            record.CredentialSource == AuditCredentialSource.Unspecified ||
            record.OperationKind == AuditOperationKind.Unspecified ||
            record.SensitivityLevel == AuditSensitivityLevel.Unspecified ||
            record.CapturePlane == AuditCapturePlane.Unspecified ||
            record.Outcome == AuditOutcome.Unspecified)
        {
            throw new ArgumentException("Audit enum fields must be specified.", nameof(record));
        }
    }

    private static string CleanText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static void RejectSecretCarrier(string key, string value)
    {
        var normalizedKey = key.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        if (SecretKeyFragments.Any(fragment => normalizedKey.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Audit annotation key '{key}' is not allowed.");
        }

        if (LooksLikeBearer(value) || LooksLikePrivateKey(value) || LooksLikeRawCredential(value))
        {
            throw new ArgumentException($"Audit annotation value for '{key}' looks secret-bearing.");
        }
    }

    private static bool LooksLikeBearer(string value)
    {
        return value.TrimStart().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePrivateKey(string value)
    {
        return value.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeRawCredential(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length < 16)
            return false;

        return normalized.StartsWith("nyx_", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("sk_", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("ek_", StringComparison.OrdinalIgnoreCase);
    }
}
