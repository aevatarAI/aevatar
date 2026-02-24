using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryDocumentProjectionStore<TReadModel, TKey>(
        this IServiceCollection services,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, object?>? listSortSelector = null,
        int listTakeMax = 200)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionDocumentStore<TReadModel, TKey>>(provider =>
            new InMemoryProjectionDocumentStore<TReadModel, TKey>(
                keySelector,
                keyFormatter,
                listSortSelector,
                listTakeMax,
                provider.GetService<ILogger<InMemoryProjectionDocumentStore<TReadModel, TKey>>>()));

        return services;
    }

    public static IServiceCollection AddInMemoryGraphProjectionStore(
        this IServiceCollection services)
    {
        services.AddSingleton<IProjectionGraphStore, InMemoryProjectionGraphStore>();

        return services;
    }
}
