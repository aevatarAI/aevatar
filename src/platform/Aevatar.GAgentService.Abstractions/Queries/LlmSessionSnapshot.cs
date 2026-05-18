using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record LlmSessionSnapshot(
    string ResponseId,
    string ScopeId,
    string OwnerSubject,
    LlmSessionOriginKind OriginKind,
    string? PreviousResponseId,
    LlmSessionStatus Status,
    DateTimeOffset CreatedAt,
    TimeSpan Ttl,
    DateTimeOffset? CancelledAt,
    string ActorId,
    long StateVersion,
    string LastEventId,
    IReadOnlyList<LlmSessionForwardedToolCallSnapshot>? ForwardedToolCalls = null);

public sealed record LlmSessionForwardedToolCallSnapshot(
    string CallId,
    string ToolName,
    string SchemaHash,
    string ArgumentsJson,
    LlmSessionForwardedToolCallStatus Status,
    DateTimeOffset? Expiry,
    string? ResultJson,
    DateTimeOffset? EmittedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? ResolvedAt);
