using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public sealed class WorkflowImplicitModuleDependencyExpander : IWorkflowModuleDependencyExpander
{
    public int Order => 200;

    public void Expand(WorkflowDefinition? workflow, ISet<string> moduleNames)
    {
        if (moduleNames.Contains("parallel") ||
            moduleNames.Contains("parallel_fanout") ||
            moduleNames.Contains("fan_out") ||
            moduleNames.Contains("race") ||
            moduleNames.Contains("select") ||
            moduleNames.Contains("map_reduce") ||
            moduleNames.Contains("mapreduce") ||
            moduleNames.Contains("cache") ||
            moduleNames.Contains("evaluate") ||
            moduleNames.Contains("judge") ||
            moduleNames.Contains("reflect"))
        {
            moduleNames.Add("llm_call");
        }
    }
}
