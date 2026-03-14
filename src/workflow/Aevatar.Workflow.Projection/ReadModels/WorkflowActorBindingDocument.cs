using Aevatar.Foundation.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.ReadModels;

public sealed class WorkflowActorBindingDocument
    : AevatarReadModelBase,
      IProjectionReadModel,
      IProjectionReadModelCloneable<WorkflowActorBindingDocument>
{
    public string ActorId { get; set; } = string.Empty;
    public WorkflowActorKind ActorKind { get; set; } = WorkflowActorKind.Unsupported;
    public string DefinitionActorId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string WorkflowYaml { get; set; } = string.Empty;
    public Dictionary<string, string> InlineWorkflowYamls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public WorkflowActorBindingDocument DeepClone() =>
        new()
        {
            Id = Id,
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ActorId = ActorId,
            ActorKind = ActorKind,
            DefinitionActorId = DefinitionActorId,
            RunId = RunId,
            WorkflowName = WorkflowName,
            WorkflowYaml = WorkflowYaml,
            InlineWorkflowYamls = new Dictionary<string, string>(InlineWorkflowYamls, StringComparer.OrdinalIgnoreCase),
        };
}
