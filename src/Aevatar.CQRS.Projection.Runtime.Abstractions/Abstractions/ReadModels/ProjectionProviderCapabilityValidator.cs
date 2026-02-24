namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public static class ProjectionProviderCapabilityValidator
{
    public static IReadOnlyList<string> Validate(
        ProjectionStoreRequirements requirements,
        ProjectionProviderCapabilities capabilities)
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
            else
            {
                var missingKinds = requirements.RequiredIndexKinds
                    .Where(kind => !capabilities.IndexKinds.Contains(kind))
                    .ToList();
                if (missingKinds.Count > 0)
                {
                    violations.Add(
                        $"required index kinds [{string.Join(", ", requirements.RequiredIndexKinds)}] are not fully supported by provider kinds [{string.Join(", ", capabilities.IndexKinds)}]; missing kinds [{string.Join(", ", missingKinds)}]");
                }
            }
        }

        if (requirements.RequiresAliases && !capabilities.SupportsAliases)
            violations.Add("requires alias support, but provider does not support aliases");

        if (requirements.RequiresSchemaValidation && !capabilities.SupportsSchemaValidation)
            violations.Add("requires schema validation, but provider does not support schema validation");

        if (requirements.RequiresGraph && !capabilities.SupportsGraph)
            violations.Add("requires graph storage, but provider does not support graph storage");

        if (requirements.RequiresGraphTraversal && !capabilities.SupportsGraphTraversal)
            violations.Add("requires graph traversal, but provider does not support graph traversal");

        return violations;
    }

    public static void EnsureSupported(
        Type readModelType,
        ProjectionStoreRequirements requirements,
        ProjectionProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(readModelType);
        var violations = Validate(requirements, capabilities);
        if (violations.Count == 0)
            return;

        throw new ProjectionProviderCapabilityValidationException(
            readModelType,
            requirements,
            capabilities,
            violations);
    }
}
