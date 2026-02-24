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

        var readModelRequirements = _bindingResolver.Resolve(options.ReadModelBindings, typeof(WorkflowExecutionReport));
        var readModelSelectionOptions = new ProjectionReadModelStoreSelectionOptions
        {
            RequestedProviderName = NormalizeProviderName(options.ReadModelProvider),
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };
        var relationRequirements = BuildRelationRequirements(readModelRequirements);
        var relationSelectionOptions = new ProjectionReadModelStoreSelectionOptions
        {
            RequestedProviderName = NormalizeProviderName(
                options.RelationProvider,
                options.ReadModelProvider),
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };

        return new WorkflowReadModelSelectionPlan(
            readModelRequirements,
            readModelSelectionOptions,
            relationRequirements,
            relationSelectionOptions);
    }

    private static ProjectionReadModelRequirements BuildRelationRequirements(
        ProjectionReadModelRequirements readModelRequirements)
    {
        // Workflow relation endpoints depend on relation storage + traversal as first-class capability.
        return new ProjectionReadModelRequirements(
            requiresRelations: true,
            requiresRelationTraversal: true,
            requiresAliases: readModelRequirements.RequiresAliases,
            requiresSchemaValidation: readModelRequirements.RequiresSchemaValidation);
    }

    private static string NormalizeProviderName(string providerName, string fallbackProviderName = "")
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            if (string.IsNullOrWhiteSpace(fallbackProviderName))
                return ProjectionReadModelProviderNames.InMemory;
            return fallbackProviderName.Trim();
        }

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
