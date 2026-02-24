namespace Aevatar.CQRS.Projection.Abstractions;

public static class ProjectionReadModelCapabilityValidator
{
    public static IReadOnlyList<string> Validate(
        ProjectionReadModelRequirements requirements,
        ProjectionReadModelProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(capabilities);

        var violations = new List<string>();

        if (requirements.RequiresIndexing && !capabilities.SupportsIndexing)
            violations.Add("requires indexing, but provider does not support indexing");

        if (requirements.RequiredIndexKinds.Count > 0)
        {
            if (!capabilities.SupportsIndexing)
            {
                violations.Add(
                    $"requires index kinds [{string.Join(", ", requirements.RequiredIndexKinds)}], but provider indexing is disabled");
            }
            else if (!requirements.RequiredIndexKinds.Overlaps(capabilities.IndexKinds))
            {
                violations.Add(
                    $"required index kinds [{string.Join(", ", requirements.RequiredIndexKinds)}] are not supported by provider kinds [{string.Join(", ", capabilities.IndexKinds)}]");
            }
        }

        if (requirements.RequiresAliases && !capabilities.SupportsAliases)
            violations.Add("requires alias support, but provider does not support aliases");

        if (requirements.RequiresSchemaValidation && !capabilities.SupportsSchemaValidation)
            violations.Add("requires schema validation, but provider does not support schema validation");

        if (requirements.RequiresRelations && !capabilities.SupportsRelations)
            violations.Add("requires relation storage, but provider does not support relations");

        if (requirements.RequiresRelationTraversal && !capabilities.SupportsRelationTraversal)
            violations.Add("requires relation traversal, but provider does not support relation traversal");

        return violations;
    }

    public static void EnsureSupported(
        Type readModelType,
        ProjectionReadModelRequirements requirements,
        ProjectionReadModelProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(readModelType);
        var violations = Validate(requirements, capabilities);
        if (violations.Count == 0)
            return;

        throw new ProjectionReadModelCapabilityValidationException(
            readModelType,
            requirements,
            capabilities,
            violations);
    }
}
