namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreSelectionPlanner : IProjectionStoreSelectionPlanner
{
    public ProjectionStoreSelectionPlan Build(
        IProjectionStoreSelectionRuntimeOptions options,
        Type readModelType,
        ProjectionReadModelRequirements relationRequirements)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readModelType);
        ArgumentNullException.ThrowIfNull(relationRequirements);
        EnsureReadModelModeSupported(options.ReadModelMode);

        var readModelRequirements = BuildReadModelRequirements(readModelType);
        var readModelRequiresGraph = typeof(IGraphReadModel).IsAssignableFrom(readModelType);
        var readModelProvider = NormalizeRequiredProviderName(options.DocumentProvider);
        var readModelSelectionOptions = new ProjectionReadModelStoreSelectionOptions
        {
            RequestedProviderName = readModelProvider,
            FailOnUnsupportedCapabilities = options.FailOnUnsupportedCapabilities,
        };

        var mergedRelationRequirements = MergeRelationRequirements(relationRequirements, readModelRequiresGraph);
        var relationSelectionOptions = new ProjectionReadModelStoreSelectionOptions
        {
            RequestedProviderName = NormalizeGraphProviderName(
                options.GraphProvider,
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
        ProjectionReadModelRequirements relationRequirements,
        bool readModelRequiresGraph)
    {
        return new ProjectionReadModelRequirements(
            requiresIndexing: relationRequirements.RequiresIndexing,
            requiredIndexKinds: relationRequirements.RequiredIndexKinds,
            requiresAliases: relationRequirements.RequiresAliases,
            requiresSchemaValidation: relationRequirements.RequiresSchemaValidation,
            requiresRelations: relationRequirements.RequiresRelations || readModelRequiresGraph,
            requiresRelationTraversal: relationRequirements.RequiresRelationTraversal || readModelRequiresGraph);
    }

    private static ProjectionReadModelRequirements BuildReadModelRequirements(Type readModelType)
    {
        var requiredIndexKinds = new List<ProjectionReadModelIndexKind>();

        if (typeof(IDocumentReadModel).IsAssignableFrom(readModelType))
            requiredIndexKinds.Add(ProjectionReadModelIndexKind.Document);

        return new ProjectionReadModelRequirements(
            requiresIndexing: requiredIndexKinds.Count > 0,
            requiredIndexKinds: requiredIndexKinds);
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

    private static string NormalizeGraphProviderName(
        string graphProviderName,
        string fallbackProviderName)
    {
        if (string.IsNullOrWhiteSpace(graphProviderName))
            return fallbackProviderName;

        return graphProviderName.Trim();
    }

    private static void EnsureReadModelModeSupported(ProjectionReadModelMode readModelMode)
    {
        if (readModelMode != ProjectionReadModelMode.StateOnly)
            return;

        throw new InvalidOperationException(
            "Projection store selection does not support Projection:Document:Mode=StateOnly. " +
            "Use CustomReadModel or DefaultReadModel.");
    }
}
