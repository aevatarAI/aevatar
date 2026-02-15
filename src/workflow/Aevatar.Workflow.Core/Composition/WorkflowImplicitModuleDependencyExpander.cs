using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public sealed class WorkflowImplicitModuleDependencyExpander : IWorkflowModuleDependencyExpander
{
    public int Order => 200;

    public void Expand(WorkflowDefinition? workflow, ISet<string> moduleNames)
    {
        // parallel fanout emits llm_call sub-steps.
        if (moduleNames.Contains("parallel") ||
            moduleNames.Contains("parallel_fanout") ||
            moduleNames.Contains("fan_out"))
        {
            moduleNames.Add("llm_call");
        }
    }
}
