namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreRegistration<out TStore>
{
    string ProviderName { get; }

    TStore Create(IServiceProvider serviceProvider);
}
