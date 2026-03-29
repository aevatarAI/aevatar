namespace Aevatar.GAgentService.Abstractions;

public enum ScopeBindingImplementationKind
{
    Unspecified = 0,
    Workflow = 1,
    Scripting = 2,
    GAgent = 3,
}

public sealed record ScopeBindingWorkflowSpec(
    IReadOnlyList<string> WorkflowYamls);

public sealed record ScopeBindingScriptSpec(
    string ScriptId,
    string? ScriptRevision = null);

public sealed record ScopeBindingGAgentEndpoint(
    string EndpointId,
    string DisplayName,
    ServiceEndpointKind Kind,
    string RequestTypeUrl,
    string ResponseTypeUrl,
    string Description);

public sealed record ScopeBindingGAgentSpec(
    string ActorTypeName,
    string? PreferredActorId,
    IReadOnlyList<ScopeBindingGAgentEndpoint> Endpoints);

public sealed record ScopeBindingUpsertRequest(
    string ScopeId,
    ScopeBindingImplementationKind ImplementationKind,
    ScopeBindingWorkflowSpec? Workflow = null,
    ScopeBindingScriptSpec? Script = null,
    ScopeBindingGAgentSpec? GAgent = null,
    string? DisplayName = null,
    string? RevisionId = null,
    string? AppId = null);

public sealed record ScopeBindingWorkflowResult(
    string WorkflowName,
    string DefinitionActorIdPrefix);

public sealed record ScopeBindingScriptResult(
    string ScriptId,
    string ScriptRevision,
    string DefinitionActorId);

public sealed record ScopeBindingGAgentResult(
    string ActorTypeName,
    string PreferredActorId);

public sealed record ScopeBindingUpsertResult(
    string ScopeId,
    string ServiceId,
    string DisplayName,
    string RevisionId,
    ScopeBindingImplementationKind ImplementationKind,
    string ExpectedActorId,
    string WorkflowName = "",
    string DefinitionActorIdPrefix = "",
    ScopeBindingWorkflowResult? Workflow = null,
    ScopeBindingScriptResult? Script = null,
    ScopeBindingGAgentResult? GAgent = null);
