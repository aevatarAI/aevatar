namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionReadModelRuntimeOptions
{
    public ProjectionReadModelRuntimeOptions()
    {
        Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public string Provider { get; set; } = ProjectionReadModelProviderNames.InMemory;

    public bool FailOnUnsupportedCapabilities { get; set; } = true;

    public Dictionary<string, string> Bindings { get; }
}
