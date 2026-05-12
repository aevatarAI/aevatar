using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ResponseSessionSnapshot(
    string ResponseId,
    string ScopeId,
    string OwnerSubject,
    ResponseSessionOriginKind OriginKind,
    string? PreviousResponseId,
    ResponseSessionStatus Status,
    DateTimeOffset CreatedAt,
    TimeSpan Ttl,
    DateTimeOffset? CancelledAt,
    string ActorId,
    long StateVersion,
    string LastEventId,
    IReadOnlyList<ResponseSessionForwardedToolCallSnapshot>? ForwardedToolCalls = null);

public sealed record ResponseSessionForwardedToolCallSnapshot(
    string CallId,
    string ToolName,
    string SchemaHash,
    string ArgumentsJson,
    ResponseSessionForwardedToolCallStatus Status,
    DateTimeOffset? Expiry,
    string? ResultJson,
    DateTimeOffset? EmittedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? ResolvedAt);
