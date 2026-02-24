namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionGraphRuntimeOptions
{
    public string ProviderName { get; set; } = ProjectionProviderNames.InMemory;

    public bool FailFastOnStartup { get; set; } = true;
}
