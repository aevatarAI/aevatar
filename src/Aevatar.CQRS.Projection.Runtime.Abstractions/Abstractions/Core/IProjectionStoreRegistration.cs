namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreRegistration<out TStore>
{
    string ProviderName { get; }

    bool IsPrimaryQueryStore { get; }

    TStore Create(IServiceProvider serviceProvider);
}
