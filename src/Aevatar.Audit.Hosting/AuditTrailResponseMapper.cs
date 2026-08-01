using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Hosting;

internal static class AuditTrailResponseMapper
{
    public const string CloudEventsSpecVersion = "1.0";
    public const string CloudEventsBatchContentType = "application/cloudevents-batch+json";

    public static AuditTrailReadResponse ToResponse(AuditTrailPage page) =>
        new(
            page.Records.Select(ToRecordResponse).ToArray(),
            ToCoverageResponse(page));

    public static IReadOnlyList<AuditCloudEventResponse> ToCloudEvents(AuditTrailPage page) =>
        page.Records.Select(ToCloudEvent).ToArray();

    public static AuditTrailRecordResponse ToRecordResponse(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var lifecyclePhase = AuditContractSemantics.ResolveLifecyclePhase(record);
        var terminalOutcome = AuditContractSemantics.ResolveTerminalOutcome(record);
        var compatibility = AuditContractSemantics.GetSchemaCompatibility(record);
        var hasCurrentContract = compatibility == AuditRecordSchemaCompatibility.Current;
        var eventKind = Optional(record.EventKind) ?? record.OperationName;
        var subject = Optional(record.Subject) ?? BuildSubject(record.Target);
        var source = ResolveSource(record);

        return new AuditTrailRecordResponse(
            record.AuditId,
            eventKind,
            subject,
            source,
            AuditContractSemantics.ResolveSchemaVersion(record),
            SchemaCompatibilityName(compatibility),
            ToDateTimeOffset(record.OccurredAt),
            record.RecordedAt is null ? ToDateTimeOffset(record.OccurredAt) : ToDateTimeOffset(record.RecordedAt),
            LifecyclePhaseName(lifecyclePhase),
            terminalOutcome == AuditTerminalOutcome.Unspecified ? null : TerminalOutcomeName(terminalOutcome),
            record.ScopeId,
            record.AuditActorId,
            record.IdentityKeyId,
            record.ActorKind.ToString(),
            record.CredentialSource.ToString(),
            record.OperationKind.ToString(),
            record.OperationName,
            record.SensitivityLevel.ToString(),
            record.Outcome.ToString(),
            record.CapturePlane.ToString(),
            ToTarget(record.Target, hasCurrentContract),
            ToCorrelation(record.Correlation, hasCurrentContract),
            ToFailure(record, terminalOutcome, hasCurrentContract),
            hasCurrentContract ? ToProvenance(record) : null,
            hasCurrentContract ? ToRedaction(record.Redaction) : null,
            hasCurrentContract ? ToToolExecution(record.ToolExecution) : null,
            hasCurrentContract ? ToCommittedFact(record.CommittedFactRef) : null,
            hasCurrentContract ? Optional(record.RequestSummary) : null,
            hasCurrentContract ? Optional(record.ResultSummary) : null);
    }

    private static AuditCloudEventResponse ToCloudEvent(AuditRecord record)
    {
        var data = ToRecordResponse(record);
        return new AuditCloudEventResponse
        {
            SpecVersion = CloudEventsSpecVersion,
            Id = data.Id,
            Source = data.Source,
            Type = data.EventKind,
            Subject = data.Subject,
            Time = data.OccurredAtUtc,
            DataSchema = BuildDataSchema(data.SchemaVersion),
            Traceparent = data.Correlation?.Traceparent,
            Tracestate = data.Correlation?.Tracestate,
            CorrelationId = data.Correlation?.CorrelationId,
            CausationId = data.Correlation?.CausationId,
            Data = data,
        };
    }

    internal static AuditQueryCoverageResponse ToCoverageResponse(AuditTrailPage page) =>
        new(
            new AuditQueryWindowResponse(page.Coverage.RequestedWindow.From, page.Coverage.RequestedWindow.To),
            new AuditQueryWindowResponse(page.Coverage.EffectiveWindow.From, page.Coverage.EffectiveWindow.To),
            page.NextCursor,
            page.Coverage.Truncated,
            page.Coverage.IngestionWatermark,
            page.Coverage.CompleteThrough,
            WindowCompletenessName(page.Coverage.WindowCompleteness),
            SchemaCompatibilityName(page.Coverage.SchemaCompatibility),
            page.ReadAt);

    private static AuditTargetResponse? ToTarget(AuditTarget? target, bool hasCurrentContract) =>
        target is null
            ? null
            : new AuditTargetResponse(
                target.Kind,
                target.Id,
                hasCurrentContract ? Optional(target.DisplayName) : null);

    private static AuditCorrelationResponse? ToCorrelation(
        AuditCorrelation? correlation,
        bool hasCurrentContract) =>
        correlation is null
            ? null
            : new AuditCorrelationResponse(
                Optional(correlation.TraceId),
                hasCurrentContract ? Optional(correlation.SpanId) : null,
                hasCurrentContract ? Optional(correlation.Traceparent) : null,
                hasCurrentContract ? Optional(correlation.Tracestate) : null,
                hasCurrentContract ? Optional(correlation.CorrelationId) : null,
                hasCurrentContract ? Optional(correlation.CausationId) : null,
                Optional(correlation.RequestId),
                hasCurrentContract ? Optional(correlation.CommandId) : null,
                hasCurrentContract ? Optional(correlation.CallId) : null,
                hasCurrentContract ? Optional(correlation.SessionId) : null,
                hasCurrentContract ? Optional(correlation.WorkflowRunId) : null,
                hasCurrentContract ? Optional(correlation.ApprovalId) : null);

    private static AuditFailureResponse? ToFailure(
        AuditRecord record,
        AuditTerminalOutcome terminalOutcome,
        bool hasCurrentContract)
    {
        if (hasCurrentContract && record.Failure is { } failure)
        {
            return new AuditFailureResponse(
                failure.Code,
                FailureCategoryName(failure.Category),
                RetryabilityName(failure.Retryability),
                LifecyclePhaseName(failure.FailedPhase),
                Optional(failure.SanitizedMessage));
        }

        if (terminalOutcome != AuditTerminalOutcome.Failed)
            return null;

        return new AuditFailureResponse(
            "legacy_failure",
            "legacy_unspecified",
            "unknown",
            "legacy_unspecified",
            null);
    }

    private static AuditExecutionProvenanceResponse? ToProvenance(AuditRecord record)
    {
        var provenance = record.Provenance;
        if (provenance is null && record.CommittedFactRef is null && record.Correlation is null)
            return null;

        return new AuditExecutionProvenanceResponse(
            FirstOptional(provenance?.ScopeId, record.ScopeId),
            Optional(provenance?.TeamId),
            Optional(provenance?.MemberId),
            Optional(provenance?.WorkflowId),
            Optional(provenance?.PublishedServiceId),
            FirstOptional(provenance?.RunId, record.Correlation?.WorkflowRunId),
            FirstOptional(provenance?.CausationId, record.Correlation?.CausationId),
            FirstOptional(provenance?.CorrelationId, record.Correlation?.CorrelationId),
            FirstOptional(provenance?.ActorId, record.CommittedFactRef?.ActorId),
            ResolveActorStateVersion(provenance, record.CommittedFactRef),
            FirstOptional(provenance?.ActorEventId, record.CommittedFactRef?.CommittedEventId),
            ToChatProvenance(provenance?.Chat));
    }

    private static AuditChatProvenanceResponse? ToChatProvenance(AuditChatProvenance? chat) =>
        chat?.Surface switch
        {
            AuditChatSurface.NyxidAssistant => new AuditChatProvenanceResponse(
                "nyxid_assistant",
                Optional(chat.ConversationId),
                Optional(chat.TurnId),
                Optional(chat.TaskId),
                Optional(chat.StepId),
                Optional(chat.ActionRequestId)),
            AuditChatSurface.WorkflowChat => new AuditChatProvenanceResponse(
                "workflow_chat",
                Optional(chat.ConversationId),
                Optional(chat.TurnId),
                Optional(chat.TaskId),
                Optional(chat.StepId),
                Optional(chat.ActionRequestId)),
            _ => null,
        };

    private static AuditRedactionResponse? ToRedaction(AuditRedaction? redaction) =>
        redaction is null
            ? null
            : new AuditRedactionResponse(
                redaction.Policy,
                redaction.OmittedFields.ToArray(),
                redaction.ValuesSanitized);

    private static AuditToolExecutionResponse? ToToolExecution(AuditToolExecution? toolExecution) =>
        toolExecution is null
            ? null
            : new AuditToolExecutionResponse(
                toolExecution.ArgumentsSha256,
                ToolExecutionPhaseName(toolExecution.ExecutionPhase),
                toolExecution.IsMutation);

    private static AuditCommittedFactReferenceResponse? ToCommittedFact(AuditCommittedFactReference? reference) =>
        reference is null
            ? null
            : new AuditCommittedFactReferenceResponse(
                Optional(reference.CommittedEventId),
                Optional(reference.ActorId),
                Optional(reference.ActorType),
                Optional(reference.EventTypeUrl),
                reference.StateVersion > 0 ? reference.StateVersion : null);

    private static string ResolveSource(AuditRecord record)
    {
        var source = Optional(record.Source);
        if (source is not null && Uri.IsWellFormedUriString(source, UriKind.Absolute))
            return source;

        return record.CapturePlane switch
        {
            AuditCapturePlane.BoundaryEndpoint => "urn:aevatar:audit:boundary-endpoint",
            AuditCapturePlane.ToolExecution => "urn:aevatar:audit:tool-execution",
            AuditCapturePlane.ProjectionArtifact => "urn:aevatar:audit:projection-artifact",
            _ => "urn:aevatar:audit:legacy",
        };
    }

    private static string BuildSubject(AuditTarget? target) =>
        target is null ? "audit/unknown" : $"{target.Kind}/{target.Id}";

    private static string BuildDataSchema(string schemaVersion) =>
        $"https://schemas.aevatar.ai/audit/{Uri.EscapeDataString(schemaVersion)}";

    private static string LifecyclePhaseName(AuditLifecyclePhase value) => value switch
    {
        AuditLifecyclePhase.Accepted => "accepted",
        AuditLifecyclePhase.Running => "running",
        AuditLifecyclePhase.WaitingApproval => "waiting_approval",
        AuditLifecyclePhase.Terminal => "terminal",
        _ => "unspecified",
    };

    private static string TerminalOutcomeName(AuditTerminalOutcome value) => value switch
    {
        AuditTerminalOutcome.Succeeded => "succeeded",
        AuditTerminalOutcome.Failed => "failed",
        AuditTerminalOutcome.Cancelled => "cancelled",
        AuditTerminalOutcome.TimedOut => "timed_out",
        _ => "unspecified",
    };

    private static string ToolExecutionPhaseName(AuditToolExecutionPhase value) => value switch
    {
        AuditToolExecutionPhase.Running => "running",
        AuditToolExecutionPhase.WaitingApproval => "waiting_approval",
        AuditToolExecutionPhase.Terminal => "terminal",
        _ => "unspecified",
    };

    private static string FailureCategoryName(AuditFailureCategory value) => value switch
    {
        AuditFailureCategory.Authorization => "authorization",
        AuditFailureCategory.Validation => "validation",
        AuditFailureCategory.Execution => "execution",
        AuditFailureCategory.Dependency => "dependency",
        AuditFailureCategory.Timeout => "timeout",
        AuditFailureCategory.Conflict => "conflict",
        AuditFailureCategory.Internal => "internal",
        _ => "unspecified",
    };

    private static string RetryabilityName(AuditRetryability value) => value switch
    {
        AuditRetryability.Unknown => "unknown",
        AuditRetryability.Retryable => "retryable",
        AuditRetryability.NotRetryable => "not_retryable",
        _ => "unspecified",
    };

    private static string WindowCompletenessName(AuditWindowCompleteness value) => value switch
    {
        AuditWindowCompleteness.Complete => "complete",
        AuditWindowCompleteness.BehindIngestionWatermark => "behind_ingestion_watermark",
        AuditWindowCompleteness.Unbounded => "unbounded",
        _ => "unknown",
    };

    private static string SchemaCompatibilityName(AuditSchemaCompatibility value) => value switch
    {
        AuditSchemaCompatibility.Current => "current",
        AuditSchemaCompatibility.ContainsLegacyRecords => "contains_legacy_records",
        AuditSchemaCompatibility.Incompatible => "incompatible",
        _ => "incompatible",
    };

    private static string SchemaCompatibilityName(AuditRecordSchemaCompatibility value) => value switch
    {
        AuditRecordSchemaCompatibility.Current => "current",
        AuditRecordSchemaCompatibility.LegacyMapped => "legacy_mapped",
        AuditRecordSchemaCompatibility.Incompatible => "incompatible",
        _ => "incompatible",
    };

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstOptional(params string?[] values) =>
        values.Select(Optional).FirstOrDefault(static value => value is not null);

    private static long? ResolveActorStateVersion(
        AuditExecutionProvenance? provenance,
        AuditCommittedFactReference? committedFact) =>
        provenance is { ActorStateVersion: > 0 }
            ? provenance.ActorStateVersion
            : committedFact is { StateVersion: > 0 }
                ? committedFact.StateVersion
                : null;

    private static DateTimeOffset ToDateTimeOffset(Timestamp? timestamp) =>
        timestamp is null ? DateTimeOffset.UnixEpoch : timestamp.ToDateTimeOffset();
}
