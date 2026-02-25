using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public sealed class WorkflowStepTypeModuleDependencyExpander : IWorkflowModuleDependencyExpander
{
    private static readonly HashSet<string> ClosedWorldBlockedModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "llm_call",
        "tool_call",
        "connector_call",
        "bridge_call",
        "evaluate",
        "judge",
        "reflect",
        "human_input",
        "human_approval",
        "wait_signal",
        "wait",
        "emit",
        "publish",
        "parallel",
        "parallel_fanout",
        "fan_out",
        "race",
        "select",
        "map_reduce",
        "mapreduce",
        "vote_consensus",
        "vote",
        "foreach",
        "for_each",
    };

    public int Order => 100;

    public void Expand(WorkflowDefinition? workflow, ISet<string> moduleNames)
    {
        if (workflow == null)
            return;

        CollectModuleTypesFromSteps(
            workflow.Steps,
            moduleNames,
            workflow.Configuration.ClosedWorldMode);
    }

    private static void CollectModuleTypesFromSteps(
        IEnumerable<StepDefinition> steps,
        ISet<string> moduleNames,
        bool closedWorldMode)
    {
        foreach (var step in steps)
        {
            if (!closedWorldMode || !ClosedWorldBlockedModules.Contains(step.Type))
                moduleNames.Add(step.Type);

            if (!closedWorldMode && !string.IsNullOrWhiteSpace(step.TargetRole))
                moduleNames.Add("llm_call");

            foreach (var (key, value) in step.Parameters)
            {
                if (key.EndsWith("_step_type", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    if (!closedWorldMode || !ClosedWorldBlockedModules.Contains(value))
                        moduleNames.Add(value);
                }
            }

            if ((step.Type.Equals("foreach", StringComparison.OrdinalIgnoreCase) ||
                 step.Type.Equals("for_each", StringComparison.OrdinalIgnoreCase)) &&
                !step.Parameters.ContainsKey("sub_step_type"))
            {
                if (!closedWorldMode)
                    moduleNames.Add("parallel");
            }

            if (step.Children is { Count: > 0 })
                CollectModuleTypesFromSteps(step.Children, moduleNames, closedWorldMode);
        }
    }
}
