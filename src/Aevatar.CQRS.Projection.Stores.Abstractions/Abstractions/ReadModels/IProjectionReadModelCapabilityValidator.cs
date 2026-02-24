namespace Aevatar.CQRS.Projection.Abstractions;

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
