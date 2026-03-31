namespace Aevatar.Foundation.Abstractions.ExternalLinks;

/// <summary>
/// Implemented by actors that need external long-lived connections.
/// The runtime reads descriptors during activation to set up connections.
/// </summary>
public interface IExternalLinkAware
{
    IReadOnlyList<ExternalLinkDescriptor> GetLinkDescriptors();
}
