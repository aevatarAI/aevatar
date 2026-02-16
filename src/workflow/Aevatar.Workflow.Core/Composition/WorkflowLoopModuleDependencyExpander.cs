using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public sealed class WorkflowLoopModuleDependencyExpander : IWorkflowModuleDependencyExpander
{
    public int Order => 0;

    public void Expand(WorkflowDefinition? workflow, ISet<string> moduleNames)
    {
        moduleNames.Add("workflow_loop");
    }
}
