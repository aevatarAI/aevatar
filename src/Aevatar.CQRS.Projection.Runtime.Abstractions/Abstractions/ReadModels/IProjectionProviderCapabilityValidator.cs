namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionProviderCapabilityValidator
{
    IReadOnlyList<string> Validate(
        ProjectionStoreRequirements requirements,
        ProjectionProviderCapabilities capabilities);

    void EnsureSupported(
        Type readModelType,
        ProjectionStoreRequirements requirements,
        ProjectionProviderCapabilities capabilities);
}
