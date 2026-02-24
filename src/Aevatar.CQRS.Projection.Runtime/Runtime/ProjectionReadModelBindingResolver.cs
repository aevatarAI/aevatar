namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionReadModelBindingResolver : IProjectionReadModelBindingResolver
{
    public ProjectionReadModelRequirements Resolve(
        IReadOnlyDictionary<string, string> readModelBindings,
        Type readModelType)
    {
        ArgumentNullException.ThrowIfNull(readModelBindings);
        ArgumentNullException.ThrowIfNull(readModelType);

        if (!TryGetBinding(readModelBindings, readModelType, out var bindingKey, out var bindingValue))
            return new ProjectionReadModelRequirements();

        if (!Enum.TryParse<ProjectionReadModelIndexKind>(bindingValue, true, out var indexKind) ||
            indexKind == ProjectionReadModelIndexKind.None)
        {
            throw new ProjectionReadModelBindingException(
                readModelType,
                bindingKey,
                bindingValue,
                $"Allowed values are {ProjectionReadModelIndexKind.Document} or {ProjectionReadModelIndexKind.Graph}.");
        }

        return new ProjectionReadModelRequirements(
            requiresIndexing: true,
            requiredIndexKinds: [indexKind],
            requiresRelations: indexKind == ProjectionReadModelIndexKind.Graph,
            requiresRelationTraversal: indexKind == ProjectionReadModelIndexKind.Graph);
    }

    private static bool TryGetBinding(
        IReadOnlyDictionary<string, string> readModelBindings,
        Type readModelType,
        out string bindingKey,
        out string bindingValue)
    {
        if (readModelBindings.Count == 0)
        {
            bindingKey = "";
            bindingValue = "";
            return false;
        }

        var fullName = readModelType.FullName ?? "";
        if (fullName.Length > 0 && readModelBindings.TryGetValue(fullName, out bindingValue!))
        {
            bindingKey = fullName;
            return true;
        }

        if (readModelBindings.TryGetValue(readModelType.Name, out bindingValue!))
        {
            throw new ProjectionReadModelBindingException(
                readModelType,
                readModelType.Name,
                bindingValue,
                $"Binding key must use full type name '{fullName}'.");
        }

        bindingKey = "";
        bindingValue = "";
        return false;
    }
}
