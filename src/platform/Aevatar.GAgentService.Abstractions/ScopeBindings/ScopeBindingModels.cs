namespace Aevatar.GAgentService.Abstractions;

using System.Text.Json.Serialization;

public enum ScopeBindingImplementationKind
{
    Unspecified = 0,
    Workflow = 1,
    Scripting = 2,
    GAgent = 3,
}

public sealed record ScopeBindingWorkflowSpec
{
    [JsonConstructor]
    public ScopeBindingWorkflowSpec(
        string WorkflowId,
        IReadOnlyList<string> WorkflowYamls)
    {
        this.WorkflowId = WorkflowId;
        this.WorkflowYamls = WorkflowYamls;
    }

    public ScopeBindingWorkflowSpec(IReadOnlyList<string> WorkflowYamls)
        : this(string.Empty, WorkflowYamls)
    {
    }

    public string WorkflowId { get; init; }

    public IReadOnlyList<string> WorkflowYamls { get; init; }
}

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
    string AgentKind,
    IReadOnlyList<ScopeBindingGAgentEndpoint> Endpoints);

public sealed record ScopeBindingUpsertRequest(
    string ScopeId,
    ScopeBindingImplementationKind ImplementationKind,
    ScopeBindingWorkflowSpec? Workflow = null,
    ScopeBindingScriptSpec? Script = null,
    ScopeBindingGAgentSpec? GAgent = null,
    string? DisplayName = null,
    string? RevisionId = null,
    string? AppId = null,
    string? ServiceId = null,
    bool? ExposureDesired = null,
    bool AllowExistingRevisionReplay = false,
    string? ReplayRevisionId = null)
{
    [JsonIgnore]
    public WorkflowCapabilityAdmissionContext? CapabilityAdmission { get; init; }
}

public sealed record ScopeBindingWorkflowResult
{
    [JsonConstructor]
    public ScopeBindingWorkflowResult(
        string WorkflowId,
        string WorkflowName,
        string DefinitionActorIdPrefix)
    {
        this.WorkflowId = WorkflowId;
        this.WorkflowName = WorkflowName;
        this.DefinitionActorIdPrefix = DefinitionActorIdPrefix;
    }

    public ScopeBindingWorkflowResult(
        string WorkflowName,
        string DefinitionActorIdPrefix)
        : this(string.Empty, WorkflowName, DefinitionActorIdPrefix)
    {
    }

    public string WorkflowId { get; init; }

    public string WorkflowName { get; init; }

    public string DefinitionActorIdPrefix { get; init; }
}

public sealed record ScopeBindingScriptResult(
    string ScriptId,
    string ScriptRevision,
    string DefinitionActorId)
{
    public IReadOnlyList<string> EndpointIds { get; init; } = [];
}

public sealed record ScopeBindingGAgentResult(
    string DiagnosticClrTypeName);

public sealed record ScopeBindingUpsertResult(
    string ScopeId,
    string ServiceId,
    string DisplayName,
    string RevisionId,
    ScopeBindingImplementationKind ImplementationKind,
    string ExpectedActorId,
    string AcceptanceStage = "accepted",
    string PropagationStage = "readmodel_propagating",
    string WorkflowName = "",
    string DefinitionActorIdPrefix = "",
    ScopeBindingWorkflowResult? Workflow = null,
    ScopeBindingScriptResult? Script = null,
    ScopeBindingGAgentResult? GAgent = null,
    string ExpectedDeploymentId = "");
