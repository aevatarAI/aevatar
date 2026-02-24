namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionProviderCapabilityValidatorService : IProjectionProviderCapabilityValidator
{
    public IReadOnlyList<string> Validate(
        ProjectionStoreRequirements requirements,
        ProjectionProviderCapabilities capabilities) =>
        ProjectionProviderCapabilityValidator.Validate(requirements, capabilities);

    public void EnsureSupported(
        Type readModelType,
        ProjectionStoreRequirements requirements,
        ProjectionProviderCapabilities capabilities) =>
        ProjectionProviderCapabilityValidator.EnsureSupported(readModelType, requirements, capabilities);
}
