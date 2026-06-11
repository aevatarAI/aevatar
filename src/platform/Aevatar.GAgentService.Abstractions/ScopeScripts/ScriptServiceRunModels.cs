using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.AGUI.Contracts;

namespace Aevatar.GAgentService.Abstractions.ScopeScripts;

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream inline orchestration in endpoints
//   New principle: use existing ICommandInteractionService skeleton with ScriptServiceRunCommand and Application-owned service-run registration decorator
public sealed record ScriptServiceRunCommand(
    string ScopeId,
    string ServiceId,
    string ServiceKey,
    string EndpointId,
    string RevisionId,
    string DeploymentId,
    string RuntimeActorId,
    string DefinitionActorId,
    string ScriptRevision,
    string Prompt,
    string? SessionId,
    string RunId,
    string CommandId,
    string CorrelationId,
    IReadOnlyDictionary<string, string>? Headers,
    ServiceIdentity? Identity) : ICommandContextSeed
{
    string? ICommandContextSeed.CommandId => CommandId;

    string? ICommandContextSeed.CorrelationId => CorrelationId;

    IReadOnlyDictionary<string, string>? ICommandContextSeed.Headers => Headers;
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream accepted state was assembled in endpoint-local variables
//   New principle: expose the accepted ACK as a typed receipt with separate actor, run, command, and correlation identities
public sealed record ScriptServiceRunAcceptedReceipt(
    string ActorId,
    string RunId,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AcceptedAt = default);

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream start failures surfaced as endpoint exceptions
//   New principle: model start-failure categories as typed command interaction errors
public enum ScriptServiceRunStartErrorCode
{
    None = 0,
    InvalidArgument = 1,
    RuntimeActorUnavailable = 2,
    ProjectionUnavailable = 3,
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream mixed validation, runtime, and projection failures in Host code
//   New principle: carry Application-owned failure semantics through a typed start error
public sealed record ScriptServiceRunStartError(
    ScriptServiceRunStartErrorCode Code,
    string FieldName,
    string Message)
{
    public static ScriptServiceRunStartError InvalidArgument(string fieldName, string message) =>
        new(
            ScriptServiceRunStartErrorCode.InvalidArgument,
            fieldName ?? string.Empty,
            message ?? string.Empty);

    public static ScriptServiceRunStartError RuntimeActorUnavailable(string message) =>
        new(
            ScriptServiceRunStartErrorCode.RuntimeActorUnavailable,
            "runtimeActorId",
            message ?? string.Empty);

    public static ScriptServiceRunStartError ProjectionUnavailable(string message) =>
        new(
            ScriptServiceRunStartErrorCode.ProjectionUnavailable,
            string.Empty,
            message ?? string.Empty);

    public Exception ToException() =>
        Code switch
        {
            ScriptServiceRunStartErrorCode.InvalidArgument =>
                new ArgumentException(
                    string.IsNullOrWhiteSpace(Message)
                        ? $"Invalid argument: {FieldName}."
                        : Message,
                    string.IsNullOrWhiteSpace(FieldName) ? null : FieldName),
            ScriptServiceRunStartErrorCode.RuntimeActorUnavailable =>
                new InvalidOperationException(
                    string.IsNullOrWhiteSpace(Message)
                        ? "Script runtime actor is not available."
                        : Message),
            ScriptServiceRunStartErrorCode.ProjectionUnavailable =>
                new InvalidOperationException(
                    string.IsNullOrWhiteSpace(Message)
                        ? "Script execution projection pipeline is unavailable."
                        : Message),
            _ => new InvalidOperationException(
                string.IsNullOrWhiteSpace(Message)
                    ? "Script service run failed to start."
                    : Message),
        };
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream terminal state was inferred by endpoint-local stream helpers
//   New principle: represent interaction completion with typed Application statuses
public enum ScriptServiceRunCompletionStatus
{
    Incomplete = 0,
    RunFinished = 1,
    RunError = 2,
}
