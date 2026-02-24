namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionReadModelCapabilityValidator
{
    IReadOnlyList<string> Validate(
        ProjectionReadModelRequirements requirements,
        ProjectionReadModelProviderCapabilities capabilities);

    void EnsureSupported(
        Type readModelType,
        ProjectionReadModelRequirements requirements,
        ProjectionReadModelProviderCapabilities capabilities);
}
