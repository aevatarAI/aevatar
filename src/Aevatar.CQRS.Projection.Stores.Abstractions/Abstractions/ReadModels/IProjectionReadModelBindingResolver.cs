namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionReadModelBindingResolver
{
    ProjectionReadModelRequirements Resolve(
        IReadOnlyDictionary<string, string> readModelBindings,
        Type readModelType);
}
