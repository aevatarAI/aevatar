namespace Aevatar.GAgentService.Abstractions;

public sealed record ScopeScriptCommandAcceptedHandle(
    string ActorId,
    string CommandId,
    string CorrelationId);

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

public sealed record ScopeScriptAcceptedSummary(
    string ScopeId,
    string ScriptId,
    string CatalogActorId,
    string DefinitionActorId,
    string RevisionId,
    string SourceHash,
    DateTimeOffset AcceptedAt,
    string ProposalId,
    string ExpectedBaseRevision);

public sealed record ScopeScriptUpsertResult(
    ScopeScriptAcceptedSummary AcceptedScript,
    ScopeScriptCommandAcceptedHandle DefinitionCommand,
    ScopeScriptCommandAcceptedHandle CatalogCommand)
{
    public string ScopeId => AcceptedScript.ScopeId;
    public string ScriptId => AcceptedScript.ScriptId;
    public string RevisionId => AcceptedScript.RevisionId;
    public string CatalogActorId => AcceptedScript.CatalogActorId;
    public string DefinitionActorId => AcceptedScript.DefinitionActorId;
    public string SourceHash => AcceptedScript.SourceHash;
}

public static class ScopeScriptSaveObservationStatuses
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Rejected = "rejected";
}

public sealed record ScopeScriptSaveObservationRequest(
    string RevisionId,
    string DefinitionActorId,
    string SourceHash,
    string ProposalId,
    string ExpectedBaseRevision,
    DateTimeOffset AcceptedAt);

public sealed record ScopeScriptSaveObservationResult(
    string ScopeId,
    string ScriptId,
    string Status,
    string Message,
    ScopeScriptSummary? CurrentScript)
{
    public bool IsTerminal =>
        string.Equals(Status, ScopeScriptSaveObservationStatuses.Applied, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Status, ScopeScriptSaveObservationStatuses.Rejected, StringComparison.OrdinalIgnoreCase);
}
