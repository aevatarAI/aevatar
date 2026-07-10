using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Identity;
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
    bool SubjectBearing = false);

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
        var record = new AuditRecord
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(context.ObservedAt),
            OperationName = seed.OperationName,
            OperationKind = AuditOperationKind.System,
            SensitivityLevel = seed.SensitivityLevel,
            Outcome = AuditOutcome.Success,
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
                TraceId = FirstNonBlank(seed.CorrelationId, context.CorrelationId),
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
        };
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
}
