namespace Aevatar.GAgentService.Abstractions;

public sealed record ScopeScriptUpsertRequest(
    string ScopeId,
    string ScriptId,
    string SourceText,
    string? RevisionId = null,
    string? ExpectedBaseRevision = null);

public sealed record ScopeScriptSummary(
    string ScopeId,
    string ScriptId,
    string CatalogActorId,
    string DefinitionActorId,
    string ActiveRevision,
    string ActiveSourceHash,
    DateTimeOffset UpdatedAt);

public sealed record ScopeScriptSource(
    string SourceText,
    string DefinitionActorId,
    string Revision,
    string SourceHash);

public sealed record ScopeScriptDetail(
    bool Available,
    string ScopeId,
    ScopeScriptSummary? Script,
    ScopeScriptSource? Source);

public sealed record ScopeScriptUpsertResult(
    ScopeScriptSummary Script,
    string RevisionId,
    string CatalogActorId,
    string DefinitionActorId);
