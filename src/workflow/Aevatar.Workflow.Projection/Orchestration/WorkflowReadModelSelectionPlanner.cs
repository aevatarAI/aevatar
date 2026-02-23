using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowReadModelSelectionPlanner : IWorkflowReadModelSelectionPlanner
{
    private readonly IProjectionReadModelBindingResolver _bindingResolver;

    public WorkflowReadModelSelectionPlanner(IProjectionReadModelBindingResolver bindingResolver)
    {
        _bindingResolver = bindingResolver;
    }

    public WorkflowReadModelSelectionPlan Build(WorkflowExecutionProjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureReadModelModeSupported(options.ReadModelMode);

        var requirements = _bindingResolver.Resolve(options.ReadModelBindings, typeof(WorkflowExecutionReport));
        var selectionOptions = new ProjectionReadModelStoreSelectionOptions
        {
            RequestedProviderName = NormalizeProviderName(options.ReadModelProvider),
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };

        return new WorkflowReadModelSelectionPlan(requirements, selectionOptions);
    }

    private static string NormalizeProviderName(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return ProjectionReadModelProviderNames.InMemory;

        return providerName.Trim();
    }

    private static void EnsureReadModelModeSupported(ProjectionReadModelMode readModelMode)
    {
        if (readModelMode != ProjectionReadModelMode.StateOnly)
            return;

        throw new InvalidOperationException(
            "Workflow projection does not support Projection:ReadModel:Mode=StateOnly. " +
            "Use CustomReadModel or DefaultReadModel.");
    }
}
