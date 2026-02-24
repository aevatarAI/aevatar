namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionStoreSelectionOptions
{
    public string RequestedProviderName { get; set; } = "";

    public bool FailOnUnsupportedCapabilities { get; set; } = true;
}
