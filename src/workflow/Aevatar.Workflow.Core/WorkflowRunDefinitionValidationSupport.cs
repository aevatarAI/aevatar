using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRunDefinitionValidationSupport
{
    public static List<string> Validate(
        WorkflowDefinition workflow,
        IEnumerable<string> knownModuleStepTypes,
        IEventModuleFactory<IWorkflowExecutionContext> stepExecutorFactory)
    {
        var knownStepTypes = new HashSet<string>(knownModuleStepTypes, StringComparer.OrdinalIgnoreCase);
        knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);
        ExpandKnownStepTypesFromFactory(workflow, knownStepTypes, stepExecutorFactory);

        return WorkflowValidator.Validate(
            workflow,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = knownStepTypes,
            },
            availableWorkflowNames: null);
    }

    private static void ExpandKnownStepTypesFromFactory(
        WorkflowDefinition workflow,
        ISet<string> knownStepTypes,
        IEventModuleFactory<IWorkflowExecutionContext> stepExecutorFactory)
    {
        foreach (var stepType in EnumerateReferencedStepTypes(workflow.Steps))
        {
            var canonical = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
            if (string.IsNullOrWhiteSpace(canonical) || knownStepTypes.Contains(canonical))
                continue;

            if (stepExecutorFactory.TryCreate(canonical, out _))
                knownStepTypes.Add(canonical);
        }
    }

    private static IEnumerable<string> EnumerateReferencedStepTypes(IEnumerable<StepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step.Type;

            foreach (var (key, value) in step.Parameters)
            {
                if (WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }

            if (step.Children is { Count: > 0 })
            {
                foreach (var childType in EnumerateReferencedStepTypes(step.Children))
                    yield return childType;
            }
        }
    }
}
