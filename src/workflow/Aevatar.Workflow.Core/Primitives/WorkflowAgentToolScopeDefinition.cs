namespace Aevatar.Workflow.Core.Primitives;

public sealed class WorkflowAgentToolScopeDefinition
{
    public bool RestrictAllowedToolNames { get; init; }

    public bool RestrictToolSets { get; init; }

    public List<string> AllowedToolNames { get; init; } = [];

    public List<string> ToolSetRefs { get; init; } = [];
}
