using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Core.CommittedFacts;

public sealed record CommittedAuditSeed(
    string OperationName,
    string TargetKind,
    string TargetId,
    string ScopeId = "",
    AuditSensitivityLevel SensitivityLevel = AuditSensitivityLevel.Confidential,
    bool IsDestructive = false,
    string CommandId = "",
    string RequestId = "",
    string CorrelationId = "",
    string ResultSummary = "",
    IReadOnlyDictionary<string, string>? Annotations = null,
    // When true the owning actor id embeds a raw external subject (e.g. an
    // external-identity binding keyed by platform/tenant/external_user_id). The
    // record factory then HMAC-hashes the origin actor id through
    // IAuditActorIdentityHasher before stamping it, so no raw subject can enter
    // the audit artifact (docs/canon/audit-trail.md §4 structural exclusion).
    bool SubjectBearing = false,
    AuditLifecyclePhase LifecyclePhase = AuditLifecyclePhase.Terminal,
    AuditTerminalOutcome TerminalOutcome = AuditTerminalOutcome.Succeeded,
    AuditFailure? Failure = null,
    string TeamId = "",
    string MemberId = "",
    string WorkflowId = "",
    string PublishedServiceId = "",
    string RunId = "",
    IReadOnlyList<string>? OmittedFields = null);

public static class CommittedAuditRecordFactory
{
    public static AuditRecord CreateSystemRecord(
        CommittedAuditTranslationContext context,
        CommittedAuditSeed seed,
        IAuditActorIdentityHasher? actorIdentityHasher = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(seed);

        // Resolve the origin actor id that is safe to stamp into the artifact.
        // Subject-bearing actor ids are HMAC-hashed so a raw external subject
        // never lands in CommittedFactRef.ActorId or the origin_actor_id
        // annotation. Non-subject actor ids (services, scoped registries, etc.)
        // remain readable for correlation.
        var originActorId = context.OriginActorId;
        var originIdentityKeyId = string.Empty;
        if (seed.SubjectBearing)
        {
            if (actorIdentityHasher is null)
            {
                throw new InvalidOperationException(
                    $"Subject-bearing audit seed for operation '{seed.OperationName}' requires an " +
                    $"{nameof(IAuditActorIdentityHasher)} to hash the origin actor id.");
            }

            var hashedOrigin = actorIdentityHasher.Hash(context.OriginActorId);
            originActorId = hashedOrigin.AuditActorId;
            originIdentityKeyId = hashedOrigin.IdentityKeyId;
        }

        var auditId = BuildAuditId(context, originActorId, seed.OperationName, seed.TargetKind, seed.TargetId);
        var correlationId = FirstNonBlank(seed.CorrelationId, context.CorrelationId);
        var causationId = FirstNonBlank(context.CausationId, context.StateEvent.EventId);
        var record = new AuditRecord
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(context.ObservedAt),
            RecordedAt = Timestamp.FromDateTimeOffset(context.RecordedAt ?? context.ObservedAt),
            EventKind = seed.OperationName,
            Subject = BuildSubject(seed.TargetKind, seed.TargetId),
            SchemaVersion = AuditContractSemantics.CurrentSchemaVersion,
            Source = "urn:aevatar:audit:projection-artifact",
            OperationName = seed.OperationName,
            OperationKind = AuditOperationKind.System,
            SensitivityLevel = seed.SensitivityLevel,
            Outcome = MapLegacyOutcome(seed.LifecyclePhase, seed.TerminalOutcome),
            LifecyclePhase = seed.LifecyclePhase,
            TerminalOutcome = seed.TerminalOutcome,
            ScopeId = seed.ScopeId ?? string.Empty,
            AuditActorId = "system",
            IdentityKeyId = "system",
            ActorKind = AuditActorKind.System,
            CredentialSource = AuditCredentialSource.System,
            Target = new AuditTarget
            {
                Kind = seed.TargetKind,
                Id = seed.TargetId,
            },
            Correlation = new AuditCorrelation
            {
                RequestId = FirstNonBlank(seed.RequestId, context.RequestId),
                CommandId = FirstNonBlank(seed.CommandId, context.CommandId, context.Envelope.Id),
                TraceId = context.TraceId?.Trim().ToLowerInvariant() ?? string.Empty,
                SpanId = context.SpanId?.Trim().ToLowerInvariant() ?? string.Empty,
                Traceparent = BuildTraceparent(context.TraceId, context.SpanId, context.TraceFlags),
                CorrelationId = correlationId,
                CausationId = causationId,
            },
            CapturePlane = AuditCapturePlane.ProjectionArtifact,
            CommittedFactRef = new AuditCommittedFactReference
            {
                CommittedEventId = context.StateEvent.EventId ?? string.Empty,
                ActorId = originActorId,
                EventTypeUrl = context.EventTypeUrl,
                StateVersion = context.StateEvent.Version,
            },
            ResultSummary = seed.ResultSummary ?? string.Empty,
            Provenance = new AuditExecutionProvenance
            {
                ScopeId = seed.ScopeId ?? string.Empty,
                TeamId = seed.TeamId ?? string.Empty,
                MemberId = seed.MemberId ?? string.Empty,
                WorkflowId = seed.WorkflowId ?? string.Empty,
                PublishedServiceId = seed.PublishedServiceId ?? string.Empty,
                RunId = FirstNonBlank(seed.RunId, seed.TargetKind == "workflow_run" ? seed.TargetId : string.Empty),
                CausationId = causationId,
                CorrelationId = correlationId,
                ActorId = originActorId,
                ActorStateVersion = context.StateEvent.Version,
                ActorEventId = context.StateEvent.EventId ?? string.Empty,
            },
            Redaction = new AuditRedaction
            {
                Policy = "aevatar.audit.safe-fields.v1",
                ValuesSanitized = true,
            },
        };
        record.Redaction.OmittedFields.Add(seed.OmittedFields ?? ["source_event.payload"]);
        if (seed.Failure is not null)
        {
            record.Failure = seed.Failure.Clone();
            record.ErrorCode = seed.Failure.Code;
            record.ErrorSummary = seed.Failure.SanitizedMessage;
        }
        if (seed.IsDestructive)
            record.Annotations.Add("is_destructive", "true");
        record.Annotations.Add("source_event_type_url", context.EventTypeUrl);
        record.Annotations.Add("source_event_id", context.StateEvent.EventId ?? string.Empty);
        record.Annotations.Add("origin_actor_id", originActorId);
        if (!string.IsNullOrEmpty(originIdentityKeyId))
            record.Annotations.Add("origin_actor_identity_key_id", originIdentityKeyId);
        if (seed.Annotations != null)
        {
            foreach (var annotation in seed.Annotations)
                record.Annotations[annotation.Key] = annotation.Value ?? string.Empty;
        }

        return record;
    }

    private static string BuildAuditId(
        CommittedAuditTranslationContext context,
        string originActorId,
        string operationName,
        string targetKind,
        string targetId)
    {
        var eventId = context.StateEvent.EventId;
        if (!string.IsNullOrWhiteSpace(eventId))
            return $"committed:{eventId}:{operationName}";

        // Fallback key uses the already-sanitized origin actor id so a
        // subject-bearing actor id never leaks through the audit id either.
        return $"committed:{originActorId}:{context.StateEvent.Version}:{operationName}:{targetKind}:{targetId}";
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string BuildSubject(string targetKind, string targetId) =>
        $"{targetKind?.Trim()}/{targetId?.Trim()}";

    private static AuditOutcome MapLegacyOutcome(
        AuditLifecyclePhase lifecyclePhase,
        AuditTerminalOutcome terminalOutcome)
    {
        if (lifecyclePhase != AuditLifecyclePhase.Terminal)
            return AuditOutcome.Accepted;

        return terminalOutcome switch
        {
            AuditTerminalOutcome.Succeeded => AuditOutcome.Success,
            AuditTerminalOutcome.Cancelled => AuditOutcome.Cancelled,
            _ => AuditOutcome.Error,
        };
    }

    private static string BuildTraceparent(string? traceId, string? spanId, string? traceFlags)
    {
        var normalizedTraceId = traceId?.Trim().ToLowerInvariant() ?? string.Empty;
        var normalizedSpanId = spanId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedTraceId.Length != 32 || normalizedSpanId.Length != 16)
            return string.Empty;

        var normalizedFlags = traceFlags?.Trim().ToLowerInvariant();
        if (normalizedFlags is not { Length: 2 })
            normalizedFlags = "00";

        return $"00-{normalizedTraceId}-{normalizedSpanId}-{normalizedFlags}";
    }
}
