using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Validation;

internal static class WorkflowValidationStepEnumerator
{
    public static IEnumerable<StepDefinition> Enumerate(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step.Children == null || step.Children.Count == 0)
                continue;

            foreach (var child in Enumerate(step.Children))
                yield return child;
        }
    }
}
