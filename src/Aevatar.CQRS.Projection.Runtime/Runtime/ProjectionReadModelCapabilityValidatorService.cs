namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionReadModelCapabilityValidatorService : IProjectionReadModelCapabilityValidator
{
    public IReadOnlyList<string> Validate(
        ProjectionReadModelRequirements requirements,
        ProjectionReadModelProviderCapabilities capabilities) =>
        ProjectionReadModelCapabilityValidator.Validate(requirements, capabilities);

    public void EnsureSupported(
        Type readModelType,
        ProjectionReadModelRequirements requirements,
        ProjectionReadModelProviderCapabilities capabilities) =>
        ProjectionReadModelCapabilityValidator.EnsureSupported(readModelType, requirements, capabilities);
}
