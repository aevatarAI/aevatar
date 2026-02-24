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
            RequestedProviderName = NormalizeRequiredProviderName(options.ReadModelProvider),
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };
        var relationRequirements = BuildRelationRequirements(readModelRequirements);
        var relationSelectionOptions = new ProjectionReadModelStoreSelectionOptions
        {
            RequestedProviderName = NormalizeRelationProviderName(
                options.RelationProvider,
                readModelSelectionOptions.RequestedProviderName),
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

    private static string NormalizeRequiredProviderName(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new InvalidOperationException(
                "WorkflowExecutionProjection:ReadModelProvider is required and cannot be empty.");
        }

        return providerName.Trim();
    }

    private static string NormalizeRelationProviderName(string relationProviderName, string fallbackProviderName)
    {
        if (string.IsNullOrWhiteSpace(relationProviderName))
            return fallbackProviderName;

        return relationProviderName.Trim();
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
