namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphRuntimeOptions
{
    string ProviderName { get; }

    bool FailFastOnStartup { get; }
}
