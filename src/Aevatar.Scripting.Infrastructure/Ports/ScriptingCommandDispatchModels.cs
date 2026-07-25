using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public enum ScriptingCommandStartErrorCode
{
    None = 0,
    InvalidArgument = 1,
    ActorNotFound = 2,
}

public sealed record ScriptingCommandStartError(
    ScriptingCommandStartErrorCode Code,
    string FieldName,
    string ActorId,
    string Message)
{
    public static ScriptingCommandStartError InvalidArgument(string fieldName, string message) =>
        new(
            ScriptingCommandStartErrorCode.InvalidArgument,
            fieldName ?? string.Empty,
            string.Empty,
            message ?? string.Empty);

    public static ScriptingCommandStartError ActorNotFound(string actorId, string message) =>
        new(
            ScriptingCommandStartErrorCode.ActorNotFound,
            string.Empty,
            actorId ?? string.Empty,
            message ?? string.Empty);

    public Exception ToException() =>
        Code switch
        {
            ScriptingCommandStartErrorCode.InvalidArgument =>
                new ArgumentException(
                    string.IsNullOrWhiteSpace(Message)
                        ? $"Invalid argument: {FieldName}."
                        : Message,
                    string.IsNullOrWhiteSpace(FieldName) ? null : FieldName),
            ScriptingCommandStartErrorCode.ActorNotFound =>
                new InvalidOperationException(
                    string.IsNullOrWhiteSpace(Message)
                        ? $"Actor not found: {ActorId}."
                        : Message),
            _ => new InvalidOperationException(
                string.IsNullOrWhiteSpace(Message)
                    ? "Scripting command dispatch failed."
                    : Message),
        };
}

public sealed record UpsertScriptDefinitionCommand(
    string ScriptId,
    string ScriptRevision,
    string SourceHash,
    string? DefinitionActorId,
    string? ScopeId,
    ScriptPackageSpec ScriptPackage) : ICommandContextSeed
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
    public string? CommandId =>
        ScriptingCommandIds.Build("script-definition", DefinitionActorId ?? ScriptId, ScriptRevision);

    public string? CorrelationId => ScriptRevision;

    public IReadOnlyDictionary<string, string>? Headers => null;
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: script runtime commands derived command id and correlation id from the runtime run id
//   New principle: command dispatch can carry explicit tracking ids without changing the target run identity
public sealed record RunScriptRuntimeCommand(
    string RuntimeActorId,
    string RunId,
    Any? InputPayload,
    string ScriptRevision,
    string DefinitionActorId,
    string RequestedEventType,
    string? ScopeId,
    string? ExplicitCommandId = null,
    string? ExplicitCorrelationId = null,
    string? CompletionNotificationActorId = null,
    string? CompletionNotificationDeliveryId = null,
    long CompletionNotificationExpiresAtUnixMs = 0) : ICommandContextSeed
{
    public string? CommandId => string.IsNullOrWhiteSpace(ExplicitCommandId)
        ? ScriptingCommandIds.Build("script-runtime", RuntimeActorId, RunId)
        : ExplicitCommandId;

    public string? CorrelationId => string.IsNullOrWhiteSpace(ExplicitCorrelationId)
        ? RunId
        : ExplicitCorrelationId;

    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record ProvisionScriptRuntimeCommand(
    string DefinitionActorId,
    string ScriptRevision,
    string? RuntimeActorId,
    ScriptDefinitionSnapshot DefinitionSnapshot,
    string? ScopeId) : ICommandContextSeed
{
    private string RevisionScope =>
        string.IsNullOrWhiteSpace(ScriptRevision)
            ? DefinitionSnapshot.Revision
            : ScriptRevision;

    public string? CommandId => ScriptingCommandIds.Build(
        "script-runtime-provision",
        string.IsNullOrWhiteSpace(RuntimeActorId) ? DefinitionActorId : RuntimeActorId,
        string.IsNullOrWhiteSpace(RevisionScope) ? "latest" : RevisionScope);

    public string? CorrelationId => string.IsNullOrWhiteSpace(RevisionScope) ? "latest" : RevisionScope;

    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record PromoteScriptCatalogRevisionCommand(
    string? CatalogActorId,
    string ScriptId,
    string ExpectedBaseRevision,
    string Revision,
    string DefinitionActorId,
    string SourceHash,
    string ProposalId,
    string? ScopeId) : ICommandContextSeed
{
    public string? CommandId => ScriptingCommandIds.Build("script-catalog-promote", ScriptId, Revision);

    public string? CorrelationId => string.IsNullOrWhiteSpace(ProposalId) ? CommandId : ProposalId;

    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record RollbackScriptCatalogRevisionCommand(
    string? CatalogActorId,
    string ScriptId,
    string TargetRevision,
    string Reason,
    string ProposalId,
    string ExpectedCurrentRevision,
    string? ScopeId) : ICommandContextSeed
{
    public string? CommandId => ScriptingCommandIds.Build("script-catalog-rollback", ScriptId, TargetRevision);

    public string? CorrelationId => string.IsNullOrWhiteSpace(ProposalId) ? CommandId : ProposalId;

    public IReadOnlyDictionary<string, string>? Headers => null;
}

internal static class ScriptingCommandIds
{
    public static string Build(string prefix, string scope, string value) =>
        string.Concat(
            prefix ?? string.Empty,
            ":",
            scope ?? string.Empty,
            ":",
            value ?? string.Empty);
}
