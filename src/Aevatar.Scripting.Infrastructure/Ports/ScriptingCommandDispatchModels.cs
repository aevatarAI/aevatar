using Aevatar.CQRS.Core.Abstractions.Commands;
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

public sealed record ScriptingCommandAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);

public sealed record UpsertScriptDefinitionCommand(
    string ScriptId,
    string ScriptRevision,
    string SourceText,
    string SourceHash,
    string? DefinitionActorId) : ICommandContextSeed
{
    public string? CommandId =>
        ScriptingCommandIds.Build("script-definition", DefinitionActorId ?? ScriptId, ScriptRevision);

    public string? CorrelationId => ScriptRevision;

    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record RunScriptRuntimeCommand(
    string RuntimeActorId,
    string RunId,
    Any? InputPayload,
    string ScriptRevision,
    string DefinitionActorId,
    string RequestedEventType) : ICommandContextSeed
{
    public string? CommandId => ScriptingCommandIds.Build("script-runtime", RuntimeActorId, RunId);

    public string? CorrelationId => RunId;

    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record PromoteScriptCatalogRevisionCommand(
    string? CatalogActorId,
    string ScriptId,
    string ExpectedBaseRevision,
    string Revision,
    string DefinitionActorId,
    string SourceHash,
    string ProposalId) : ICommandContextSeed
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
    string ExpectedCurrentRevision) : ICommandContextSeed
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
