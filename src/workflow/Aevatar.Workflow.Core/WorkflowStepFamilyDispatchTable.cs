using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowStepFamilyDispatchTable
{
    private readonly IReadOnlyDictionary<string, IWorkflowStepFamilyHandler> _handlersByStepType;

    public WorkflowStepFamilyDispatchTable(IEnumerable<IWorkflowStepFamilyHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var mapping = new Dictionary<string, IWorkflowStepFamilyHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);

            foreach (var rawStepType in handler.SupportedStepTypes)
            {
                var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(rawStepType);
                if (string.IsNullOrWhiteSpace(stepType))
                    throw new InvalidOperationException($"{handler.GetType().Name} declares an empty step type.");
                if (!mapping.TryAdd(stepType, handler))
                {
                    throw new InvalidOperationException(
                        $"Duplicate workflow step family handler registration for step type '{stepType}'.");
                }
            }
        }

        _handlersByStepType = mapping;
    }

    public bool TryGet(string stepType, out IWorkflowStepFamilyHandler handler)
    {
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(stepType);
        return _handlersByStepType.TryGetValue(canonicalType, out handler!);
    }
}
