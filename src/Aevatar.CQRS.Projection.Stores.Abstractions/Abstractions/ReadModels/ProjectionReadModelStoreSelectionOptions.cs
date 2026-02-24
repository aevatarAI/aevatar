namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionReadModelStoreSelectionOptions
{
    public string RequestedProviderName { get; set; } = "";

    public bool FailOnUnsupportedCapabilities { get; set; } = true;
}
