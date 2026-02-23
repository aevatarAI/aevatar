using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryReadModelStoreRegistration<TReadModel, TKey>(
        this IServiceCollection services,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, object?>? listSortSelector = null,
        int listTakeMax = 200,
        string providerName = ProjectionReadModelProviderNames.InMemory)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionReadModelStoreRegistration<TReadModel, TKey>>(
            new DelegateProjectionReadModelStoreRegistration<TReadModel, TKey>(
                providerName,
                new ProjectionReadModelProviderCapabilities(providerName, supportsIndexing: false),
                provider => new InMemoryProjectionReadModelStore<TReadModel, TKey>(
                    keySelector,
                    keyFormatter,
                    listSortSelector,
                    listTakeMax,
                    providerName,
                    provider.GetService<ILogger<InMemoryProjectionReadModelStore<TReadModel, TKey>>>())));

        return services;
    }
}
