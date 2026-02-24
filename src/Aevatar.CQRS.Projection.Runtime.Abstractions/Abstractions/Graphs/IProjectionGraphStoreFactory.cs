namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphStoreFactory
{
    IProjectionGraphStore Create(
        IServiceProvider serviceProvider,
        string? requestedProviderName = null);
}
