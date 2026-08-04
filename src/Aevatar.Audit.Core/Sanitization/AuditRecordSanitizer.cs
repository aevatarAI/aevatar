using System.Diagnostics;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
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
        sanitized.EventKind = CleanText(record.EventKind, _options.MaxSummaryLength);
        sanitized.Subject = CleanText(record.Subject, _options.MaxSummaryLength);
        sanitized.Source = CleanText(record.Source, _options.MaxSummaryLength);
        sanitized.RequestSummary = CleanText(record.RequestSummary, _options.MaxSummaryLength);
        sanitized.ResultSummary = CleanText(record.ResultSummary, _options.MaxSummaryLength);
        sanitized.ErrorCode = CleanText(record.ErrorCode, _options.MaxSummaryLength);
        sanitized.ErrorSummary = CleanText(record.ErrorSummary, _options.MaxSummaryLength);

        if (sanitized.Failure is not null)
        {
            RejectSecretCarrier("failure_code", record.Failure.Code);
            RejectSecretCarrier("failure_message", record.Failure.SanitizedMessage);
            sanitized.Failure.Code = CleanText(record.Failure.Code, _options.MaxSummaryLength);
            sanitized.Failure.SanitizedMessage = CleanText(
                record.Failure.SanitizedMessage,
                _options.MaxSummaryLength);
        }

        if (sanitized.Redaction is not null)
        {
            sanitized.Redaction.Policy = CleanText(record.Redaction.Policy, _options.MaxSummaryLength);
            sanitized.Redaction.OmittedFields.Clear();
            sanitized.Redaction.OmittedFields.Add(record.Redaction.OmittedFields
                .Select(field => CleanText(field, _options.MaxAnnotationKeyLength))
                .Where(static field => field.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static field => field, StringComparer.Ordinal));
        }

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
        ValidateIdentity(record);
        ValidateEventContract(record);
        ValidateEnumFields(record);
        ValidateLifecycle(record);
        ValidateSupplementalContracts(record);
        ValidateTraceContext(record.Correlation);
    }

    private static void ValidateIdentity(AuditRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.AuditId))
            throw new ArgumentException("AuditId is required.", nameof(record));

        if (record.OccurredAt is null || record.OccurredAt == Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch))
            throw new ArgumentException("OccurredAt is required.", nameof(record));

        if (record.RecordedAt is null || record.RecordedAt == Timestamp.FromDateTimeOffset(DateTimeOffset.UnixEpoch))
            throw new ArgumentException("RecordedAt is required.", nameof(record));

        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new ArgumentException("ScopeId is required.", nameof(record));

        if (string.IsNullOrWhiteSpace(record.AuditActorId))
            throw new ArgumentException("AuditActorId is required.", nameof(record));

        if (string.IsNullOrWhiteSpace(record.IdentityKeyId))
            throw new ArgumentException("IdentityKeyId is required.", nameof(record));

        if (string.IsNullOrWhiteSpace(record.OperationName))
            throw new ArgumentException("OperationName is required.", nameof(record));
    }

    private static void ValidateEventContract(AuditRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.EventKind) ||
            string.IsNullOrWhiteSpace(record.Subject) ||
            string.IsNullOrWhiteSpace(record.Source))
        {
            throw new ArgumentException("EventKind, Subject, and Source are required.", nameof(record));
        }

        if (!Uri.IsWellFormedUriString(record.Source, UriKind.Absolute))
        {
            throw new ArgumentException("Source must be an absolute URI.", nameof(record));
        }

        if (!string.Equals(
                record.SchemaVersion,
                AuditContractSemantics.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"SchemaVersion must be '{AuditContractSemantics.CurrentSchemaVersion}'.",
                nameof(record));
        }
    }

    private static void ValidateEnumFields(AuditRecord record)
    {
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

    private static void ValidateLifecycle(AuditRecord record)
    {
        var isTerminal = record.LifecyclePhase == AuditLifecyclePhase.Terminal;
        if (isTerminal != (record.TerminalOutcome != AuditTerminalOutcome.Unspecified))
        {
            throw new ArgumentException(
                "Terminal lifecycle records must carry exactly one terminal outcome; nonterminal records must carry none.",
                nameof(record));
        }

        var needsFailure = record.TerminalOutcome is AuditTerminalOutcome.Failed or AuditTerminalOutcome.TimedOut;
        if (needsFailure != (record.Failure is not null))
        {
            throw new ArgumentException(
                "Failed and timed-out terminal records must carry structured failure data, and other outcomes must not.",
                nameof(record));
        }

        if (record.Failure is { } failure &&
            (string.IsNullOrWhiteSpace(failure.Code) ||
             string.IsNullOrWhiteSpace(failure.SanitizedMessage) ||
             failure.Category == AuditFailureCategory.Unspecified ||
             failure.Retryability == AuditRetryability.Unspecified ||
             failure.FailedPhase is AuditLifecyclePhase.Unspecified or AuditLifecyclePhase.Terminal))
        {
            throw new ArgumentException("Structured failure fields must be specified.", nameof(record));
        }
    }

    private static void ValidateSupplementalContracts(AuditRecord record)
    {
        if (record.Provenance is { } provenance)
        {
            if (string.IsNullOrWhiteSpace(provenance.ScopeId))
                throw new ArgumentException("Execution provenance ScopeId is required when provenance is present.", nameof(record));

            if (!string.Equals(provenance.ScopeId, record.ScopeId, StringComparison.Ordinal))
                throw new ArgumentException("Execution provenance ScopeId must match the audit scope.", nameof(record));

            if (provenance.Chat is { Surface: AuditChatSurface.Unspecified })
                throw new ArgumentException("Chat provenance surface must be specified.", nameof(record));

            EnsureMatchingIfPresent(provenance.RunId, record.Correlation?.WorkflowRunId, "run identity", record);
            EnsureMatchingIfPresent(
                provenance.CorrelationId,
                record.Correlation?.CorrelationId,
                "correlation identity",
                record);
            EnsureMatchingIfPresent(
                provenance.CausationId,
                record.Correlation?.CausationId,
                "causation identity",
                record);
            EnsureMatchingIfPresent(
                provenance.ActorId,
                record.CommittedFactRef?.ActorId,
                "committed actor identity",
                record);
            EnsureMatchingIfPresent(
                provenance.ActorEventId,
                record.CommittedFactRef?.CommittedEventId,
                "committed event identity",
                record);
            if (provenance.ActorStateVersion > 0 &&
                record.CommittedFactRef?.StateVersion > 0 &&
                provenance.ActorStateVersion != record.CommittedFactRef.StateVersion)
            {
                throw new ArgumentException(
                    "Execution provenance state version must match the committed fact.",
                    nameof(record));
            }
        }

        if (record.Redaction is null ||
            string.IsNullOrWhiteSpace(record.Redaction.Policy) ||
            !record.Redaction.ValuesSanitized)
        {
            throw new ArgumentException("Redaction policy metadata is required.", nameof(record));
        }
    }

    private static void EnsureMatchingIfPresent(
        string? first,
        string? second,
        string fieldName,
        AuditRecord record)
    {
        if (!string.IsNullOrWhiteSpace(first) &&
            !string.IsNullOrWhiteSpace(second) &&
            !string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Execution provenance {fieldName} must match its canonical field.", nameof(record));
        }
    }

    private static void ValidateTraceContext(AuditCorrelation? correlation)
    {
        if (correlation is null)
            return;

        if (string.IsNullOrWhiteSpace(correlation.Traceparent))
        {
            if (!string.IsNullOrWhiteSpace(correlation.Tracestate))
                throw new ArgumentException("Tracestate requires a valid traceparent.", nameof(correlation));
            return;
        }

        if (!ActivityContext.TryParse(
                correlation.Traceparent.Trim(),
                string.IsNullOrWhiteSpace(correlation.Tracestate) ? null : correlation.Tracestate,
                isRemote: true,
                out var activityContext))
        {
            throw new ArgumentException("Trace context is not a valid W3C Trace Context value.", nameof(correlation));
        }

        if ((!string.IsNullOrWhiteSpace(correlation.TraceId) &&
             !string.Equals(
                 correlation.TraceId.Trim(),
                 activityContext.TraceId.ToString(),
                 StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(correlation.SpanId) &&
             !string.Equals(
                 correlation.SpanId.Trim(),
                 activityContext.SpanId.ToString(),
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "TraceId and SpanId must match traceparent when both are present.",
                nameof(correlation));
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
