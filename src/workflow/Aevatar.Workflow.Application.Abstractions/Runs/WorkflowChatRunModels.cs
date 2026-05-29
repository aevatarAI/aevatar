using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using System.Text.Json.Serialization;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public enum WorkflowChatInputPartKind
{
    Unspecified = 0,
    Text = 1,
    Image = 2,
    Audio = 3,
    Video = 4,
}

public sealed record WorkflowChatInputPart
{
    public required WorkflowChatInputPartKind Kind { get; init; }
    public string? Text { get; init; }
    public string? DataBase64 { get; init; }
    public string? MediaType { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
}

public enum WorkflowChatSourceKind
{
    Unspecified = 0,
    CatalogWorkflow = 1,
    DefinitionActor = 2,
    InlineYamlBundle = 3,
    Direct = 4,
}

public sealed record WorkflowChatSource(
    WorkflowChatSourceKind Kind,
    string? WorkflowName = null,
    string? ActorId = null,
    IReadOnlyList<string>? WorkflowYamls = null)
{
    public static WorkflowChatSource CatalogWorkflow(string workflowName) =>
        new(WorkflowChatSourceKind.CatalogWorkflow, WorkflowName: workflowName);

    public static WorkflowChatSource DefinitionActor(string actorId, string? workflowName = null) =>
        new(WorkflowChatSourceKind.DefinitionActor, WorkflowName: workflowName, ActorId: actorId);

    public static WorkflowChatSource InlineYamlBundle(IReadOnlyList<string> workflowYamls, string? workflowName = null, string? actorId = null) =>
        new(WorkflowChatSourceKind.InlineYamlBundle, WorkflowName: workflowName, ActorId: actorId, WorkflowYamls: workflowYamls);

    public static WorkflowChatSource Direct(string? actorId = null) =>
        new(WorkflowChatSourceKind.Direct, ActorId: actorId);
}

public sealed record WorkflowChatRunRequest(
    string Prompt,
    string? WorkflowName,
    string? ActorId,
    string? SessionId = null,
    IReadOnlyList<WorkflowChatInputPart>? InputParts = null,
    // Inline workflow YAML bundle; first item is the entry workflow.
    IReadOnlyList<string>? WorkflowYamls = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    // Refactor (iter15/cluster-029):
    //   Old pattern: scope id / channel facts fell back to metadata bag string keys.
    //   New principle: stable business semantics use typed proto field; metadata bag only for genuine open extension.
    string? ScopeId = null,
    WorkflowChatSource? Source = null,
    LLMControlContext? LlmControl = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? CommandIdSeed = null,
    string? CorrelationIdSeed = null,
    [property: JsonIgnore] WorkflowRunTargetSeed? TargetSeed = null) : ICommandContextSeed
{
    string? ICommandContextSeed.CommandId => CommandIdSeed;

    string? ICommandContextSeed.CorrelationId => CorrelationIdSeed;

    IReadOnlyDictionary<string, string>? ICommandContextSeed.Headers => Headers;
}

public sealed record WorkflowRunTargetSeed(
    string ActorId,
    string WorkflowNameForRun,
    IReadOnlyList<string>? CreatedActorIds = null,
    WorkflowChatSource? Source = null);

public enum WorkflowChatRunStartError
{
    None = 0,
    AgentNotFound = 1,
    WorkflowNotFound = 2,
    AgentTypeNotSupported = 3,
    ProjectionDisabled = 4,
    WorkflowBindingMismatch = 5,
    AgentWorkflowNotConfigured = 6,
    InvalidWorkflowYaml = 7,
    WorkflowNameMismatch = 8,
    PromptRequired = 9,
    ProjectionUnavailable = 10,
}

public enum WorkflowProjectionCompletionStatus
{
    Completed = 0,
    TimedOut = 1,
    Failed = 2,
    Stopped = 3,
    NotFound = 4,
    Disabled = 5,
    Unknown = 99,
}

public sealed record WorkflowChatRunAcceptedReceipt(
    string ActorId,
    string WorkflowName,
    string CommandId,
    string CorrelationId);
