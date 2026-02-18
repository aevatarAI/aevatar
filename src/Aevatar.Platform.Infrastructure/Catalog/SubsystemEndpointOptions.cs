namespace Aevatar.Platform.Infrastructure.Catalog;

public sealed class SubsystemEndpointOptions
{
    public string WorkflowBaseUrl { get; set; } = "http://localhost:5201";
    public string MakerBaseUrl { get; set; } = "http://localhost:5202";
}
