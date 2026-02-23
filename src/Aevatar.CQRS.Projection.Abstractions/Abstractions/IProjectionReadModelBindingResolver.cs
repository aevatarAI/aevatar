namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionReadModelBindingResolver
{
    ProjectionReadModelRequirements Resolve(
        IReadOnlyDictionary<string, string> readModelBindings,
        Type readModelType);
}
