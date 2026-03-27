namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceRevisionCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceRevisionSnapshot> Revisions,
    DateTimeOffset UpdatedAt,
    long StateVersion = 0,
    string LastEventId = "");

public sealed record ServiceRevisionSnapshot(
    string RevisionId,
    string ImplementationKind,
    string Status,
    string ArtifactHash,
    string FailureReason,
    IReadOnlyList<ServiceEndpointSnapshot> Endpoints,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? RetiredAt,
    ServiceRevisionImplementationSnapshot? Implementation = null);

public sealed record ServiceRevisionImplementationSnapshot(
    ServiceRevisionStaticSnapshot? Static = null,
    ServiceRevisionScriptingSnapshot? Scripting = null,
    ServiceRevisionWorkflowSnapshot? Workflow = null);

public sealed record ServiceRevisionStaticSnapshot(
    string ActorTypeName,
    string PreferredActorId);

public sealed record ServiceRevisionScriptingSnapshot(
    string ScriptId,
    string Revision,
    string DefinitionActorId,
    string SourceHash);

public sealed record ServiceRevisionWorkflowSnapshot(
    string WorkflowName,
    string DefinitionActorId,
    int InlineWorkflowCount);
