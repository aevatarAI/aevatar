using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public sealed class WorkflowStepTypeModuleDependencyExpander : IWorkflowModuleDependencyExpander
{
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
            var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
            if (!closedWorldMode || !WorkflowPrimitiveCatalog.IsClosedWorldBlocked(stepType))
                moduleNames.Add(stepType);

            if (!closedWorldMode && !string.IsNullOrWhiteSpace(step.TargetRole))
                moduleNames.Add("llm_call");

            foreach (var (key, value) in step.Parameters)
            {
                if (WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    var parameterStepType = WorkflowPrimitiveCatalog.ToCanonicalType(value);
                    if (!closedWorldMode || !WorkflowPrimitiveCatalog.IsClosedWorldBlocked(parameterStepType))
                        moduleNames.Add(parameterStepType);
                }
            }

            if (stepType.Equals("foreach", StringComparison.OrdinalIgnoreCase) &&
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
