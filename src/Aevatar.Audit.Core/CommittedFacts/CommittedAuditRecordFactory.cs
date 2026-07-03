using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Core.CommittedFacts;

public sealed record CommittedAuditSeed(
    string OperationName,
    string TargetKind,
    string TargetId,
    string ScopeId = "",
    AuditSensitivityLevel SensitivityLevel = AuditSensitivityLevel.Sensitive,
    bool IsDestructive = false,
    string CommandId = "",
    string RequestId = "",
    string CorrelationId = "",
    string ResultSummary = "",
    IReadOnlyDictionary<string, string>? Annotations = null);

public static class CommittedAuditRecordFactory
{
    public static AuditRecord CreateSystemRecord(
        CommittedAuditTranslationContext context,
        CommittedAuditSeed seed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(seed);

        var auditId = BuildAuditId(context, seed.OperationName, seed.TargetKind, seed.TargetId);
        var record = new AuditRecord
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(context.ObservedAt),
            OperationName = seed.OperationName,
            OperationKind = AuditOperationKind.CommittedFact,
            SensitivityLevel = seed.SensitivityLevel,
            Outcome = AuditOutcome.Committed,
            ScopeId = seed.ScopeId ?? string.Empty,
            AuditActorId = "system",
            IdentityKeyId = "system",
            ActorKind = AuditActorKind.System,
            ActorDisplay = "SYSTEM",
            CredentialSource = "system",
            TargetKind = seed.TargetKind,
            TargetId = seed.TargetId,
            TargetVersion = context.StateEvent.Version,
            RequestId = FirstNonBlank(seed.RequestId, context.RequestId),
            CommandId = FirstNonBlank(seed.CommandId, context.CommandId, context.Envelope.Id),
            CorrelationId = FirstNonBlank(seed.CorrelationId, context.CorrelationId),
            IsDestructive = seed.IsDestructive,
            ResultSummary = seed.ResultSummary ?? string.Empty,
        };
        record.Annotations.Add("source_event_type_url", context.EventTypeUrl);
        record.Annotations.Add("source_event_id", context.StateEvent.EventId ?? string.Empty);
        record.Annotations.Add("origin_actor_id", context.OriginActorId);
        if (seed.Annotations != null)
        {
            foreach (var annotation in seed.Annotations)
                record.Annotations[annotation.Key] = annotation.Value ?? string.Empty;
        }

        return record;
    }

    private static string BuildAuditId(
        CommittedAuditTranslationContext context,
        string operationName,
        string targetKind,
        string targetId)
    {
        var eventId = context.StateEvent.EventId;
        if (!string.IsNullOrWhiteSpace(eventId))
            return $"committed:{eventId}:{operationName}";

        return $"committed:{context.OriginActorId}:{context.StateEvent.Version}:{operationName}:{targetKind}:{targetId}";
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
