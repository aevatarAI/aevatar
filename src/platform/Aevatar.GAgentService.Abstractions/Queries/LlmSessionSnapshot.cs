using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Queries;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
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
    IReadOnlyList<LlmSessionForwardedToolCallSnapshot>? ForwardedToolCalls = null,
    LlmSessionCompletionSnapshot? Completion = null);

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
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

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public sealed record LlmSessionCompletionSnapshot(
    string OutputText,
    IReadOnlyList<LlmSessionCompletedToolCallSnapshot> ToolCalls,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    string? FailureMessage,
    TokenUsage? Usage = null);

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public sealed record LlmSessionCompletedToolCallSnapshot(
    string CallId,
    string ToolName,
    string? ResultJson);
