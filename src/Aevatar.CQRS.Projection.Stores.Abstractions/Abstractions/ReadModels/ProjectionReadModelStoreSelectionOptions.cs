namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionReadModelStoreSelectionOptions
{
    public string RequestedProviderName { get; set; } = "";

    public bool FailOnUnsupportedCapabilities { get; set; } = true;
}
