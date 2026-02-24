namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionDocumentRuntimeOptions
{
    public string ProviderName { get; set; } = ProjectionProviderNames.InMemory;

    public bool FailFastOnStartup { get; set; } = true;
}
