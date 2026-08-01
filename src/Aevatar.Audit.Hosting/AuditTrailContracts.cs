using System.Text.Json.Serialization;

namespace Aevatar.Audit.Hosting;

public sealed record AuditTrailReadResponse(
    IReadOnlyList<AuditTrailRecordResponse> Records,
    AuditQueryCoverageResponse Coverage);

public sealed record AuditQueryCoverageResponse(
    AuditQueryWindowResponse RequestedWindow,
    AuditQueryWindowResponse EffectiveWindow,
    string? ContinuationCursor,
    bool Truncated,
    DateTimeOffset? IngestionWatermark,
    DateTimeOffset? CompleteThrough,
    string WindowCompleteness,
    string SchemaCompatibility,
    DateTimeOffset ReadTimestampUtc);

public sealed record AuditQueryWindowResponse(DateTimeOffset? From, DateTimeOffset? To);

public sealed record AuditTrailRecordResponse(
    string Id,
    string EventKind,
    string Subject,
    string Source,
    string SchemaVersion,
    string SchemaCompatibility,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string LifecyclePhase,
    string? TerminalOutcome,
    string ScopeId,
    string AuditActorId,
    string IdentityKeyId,
    string ActorKind,
    string CredentialSource,
    string OperationKind,
    string OperationName,
    string SensitivityLevel,
    string LegacyOutcome,
    string CapturePlane,
    AuditTargetResponse? Target,
    AuditCorrelationResponse? Correlation,
    AuditFailureResponse? Failure,
    AuditExecutionProvenanceResponse? Provenance,
    AuditRedactionResponse? Redaction,
    AuditToolExecutionResponse? ToolExecution,
    AuditCommittedFactReferenceResponse? CommittedFact,
    string? RequestSummary,
    string? ResultSummary);

public sealed record AuditTargetResponse(string Kind, string Id, string? DisplayName);

public sealed record AuditCorrelationResponse(
    string? TraceId,
    string? SpanId,
    string? Traceparent,
    string? Tracestate,
    string? CorrelationId,
    string? CausationId,
    string? RequestId,
    string? CommandId,
    string? CallId,
    string? SessionId,
    string? WorkflowRunId,
    string? ApprovalId);

public sealed record AuditFailureResponse(
    string Code,
    string Category,
    string Retryability,
    string FailedPhase,
    string? SanitizedMessage);

public sealed record AuditExecutionProvenanceResponse(
    string? ScopeId,
    string? TeamId,
    string? MemberId,
    string? WorkflowId,
    string? PublishedServiceId,
    string? RunId,
    string? CausationId,
    string? CorrelationId,
    string? ActorId,
    long? ActorStateVersion,
    string? ActorEventId,
    AuditChatProvenanceResponse? Chat = null);

public sealed record AuditChatProvenanceResponse(
    string Surface,
    string? ConversationId,
    string? TurnId,
    string? TaskId,
    string? StepId,
    string? ActionRequestId);

public sealed record AuditRedactionResponse(
    string Policy,
    IReadOnlyList<string> OmittedFields,
    bool ValuesSanitized);

public sealed record AuditToolExecutionResponse(
    string ArgumentsSha256,
    string ExecutionPhase,
    bool IsMutation);

public sealed record AuditCommittedFactReferenceResponse(
    string? CommittedEventId,
    string? ActorId,
    string? ActorType,
    string? EventTypeUrl,
    long? StateVersion);

public sealed record AuditCloudEventResponse
{
    [JsonPropertyName("specversion")]
    public required string SpecVersion { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("time")]
    public required DateTimeOffset Time { get; init; }

    [JsonPropertyName("dataschema")]
    public required string DataSchema { get; init; }

    [JsonPropertyName("datacontenttype")]
    public string DataContentType { get; init; } = "application/json";

    [JsonPropertyName("traceparent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Traceparent { get; init; }

    [JsonPropertyName("tracestate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tracestate { get; init; }

    [JsonPropertyName("correlationid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("causationid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CausationId { get; init; }

    [JsonPropertyName("data")]
    public required AuditTrailRecordResponse Data { get; init; }
}

public sealed record AuditActorResolutionRequest(
    string Provider,
    string Subject);

public sealed record AuditActorResolutionResponse(
    string AuditActorId,
    string IdentityKeyId,
    DateTimeOffset ReadTimestampUtc);
