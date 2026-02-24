namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreSelectionPlanner : IProjectionStoreSelectionPlanner
{
    private readonly IProjectionReadModelBindingResolver _bindingResolver;

    public ProjectionStoreSelectionPlanner(IProjectionReadModelBindingResolver bindingResolver)
    {
        _bindingResolver = bindingResolver;
    }

    public ProjectionStoreSelectionPlan Build(
        IProjectionStoreSelectionRuntimeOptions options,
        Type readModelType,
        ProjectionReadModelRequirements relationRequirements)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readModelType);
        ArgumentNullException.ThrowIfNull(relationRequirements);
        EnsureReadModelModeSupported(options.ReadModelMode);

        var readModelRequirements = _bindingResolver.Resolve(options.ReadModelBindings, readModelType);
        var readModelProvider = NormalizeRequiredProviderName(options.ReadModelProvider);
        var readModelSelectionOptions = new ProjectionReadModelStoreSelectionOptions
        {
            RequestedProviderName = readModelProvider,
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };

        var mergedRelationRequirements = MergeRelationRequirements(readModelRequirements, relationRequirements);
        var relationSelectionOptions = new ProjectionReadModelStoreSelectionOptions
        {
            RequestedProviderName = NormalizeRelationProviderName(
                options.RelationProvider,
                readModelProvider),
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };

        return new ProjectionStoreSelectionPlan(
            readModelRequirements,
            readModelSelectionOptions,
            mergedRelationRequirements,
            relationSelectionOptions);
    }

    private static ProjectionReadModelRequirements MergeRelationRequirements(
        ProjectionReadModelRequirements readModelRequirements,
        ProjectionReadModelRequirements relationRequirements)
    {
        return new ProjectionReadModelRequirements(
            requiresIndexing: relationRequirements.RequiresIndexing,
            requiredIndexKinds: relationRequirements.RequiredIndexKinds,
            requiresAliases: relationRequirements.RequiresAliases || readModelRequirements.RequiresAliases,
            requiresSchemaValidation: relationRequirements.RequiresSchemaValidation || readModelRequirements.RequiresSchemaValidation,
            requiresRelations: relationRequirements.RequiresRelations,
            requiresRelationTraversal: relationRequirements.RequiresRelationTraversal);
    }

    private static string NormalizeRequiredProviderName(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new InvalidOperationException(
                "Projection read-model provider is required and cannot be empty.");
        }

        return providerName.Trim();
    }

    private static string NormalizeRelationProviderName(
        string relationProviderName,
        string fallbackProviderName)
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
            "Projection store selection does not support Projection:ReadModel:Mode=StateOnly. " +
            "Use CustomReadModel or DefaultReadModel.");
    }
}
