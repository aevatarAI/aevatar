namespace Aevatar.Workflow.Core.Primitives;

public sealed class WorkflowAgentToolScopeDefinition
{
    public List<string> AllowedToolNames { get; init; } = [];

    public List<string> ToolSetRefs { get; init; } = [];
}
