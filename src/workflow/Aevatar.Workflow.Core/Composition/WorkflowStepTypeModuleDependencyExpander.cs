using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Composition;

public sealed class WorkflowStepTypeModuleDependencyExpander : IWorkflowModuleDependencyExpander
{
    public int Order => 100;

    public void Expand(WorkflowDefinition? workflow, ISet<string> moduleNames)
    {
        if (workflow == null)
            return;

        CollectModuleTypesFromSteps(workflow.Steps, moduleNames);
    }

    private static void CollectModuleTypesFromSteps(
        IEnumerable<StepDefinition> steps,
        ISet<string> moduleNames)
    {
        foreach (var step in steps)
        {
            var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
            moduleNames.Add(stepType);

            if (!string.IsNullOrWhiteSpace(step.TargetRole))
                moduleNames.Add("llm_call");

            foreach (var (key, value) in step.Parameters)
            {
                if (WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    var parameterStepType = WorkflowPrimitiveCatalog.ToCanonicalType(value);
                    moduleNames.Add(parameterStepType);
                }
            }

            if (stepType.Equals("foreach", StringComparison.OrdinalIgnoreCase) &&
                !step.Parameters.ContainsKey("sub_step_type"))
            {
                moduleNames.Add("parallel");
            }

            if (step.Children is { Count: > 0 })
                CollectModuleTypesFromSteps(step.Children, moduleNames);
        }
    }
}
