using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;

public static class InMemoryProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryDocumentProjectionStore<TReadModel, TKey>(
        this IServiceCollection services,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, object?>? defaultSortSelector = null,
        int queryTakeMax = 200)
        where TReadModel : class, IProjectionReadModel
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<InMemoryProjectionDocumentStore<TReadModel, TKey>>(provider =>
            new InMemoryProjectionDocumentStore<TReadModel, TKey>(
                keySelector,
                keyFormatter,
                defaultSortSelector,
                queryTakeMax,
                provider.GetService<ILogger<InMemoryProjectionDocumentStore<TReadModel, TKey>>>()));
        services.AddSingleton<IProjectionDocumentWriter<TReadModel>>(provider =>
            provider.GetRequiredService<InMemoryProjectionDocumentStore<TReadModel, TKey>>());
        services.AddSingleton<IProjectionDocumentReader<TReadModel, TKey>>(provider =>
            provider.GetRequiredService<InMemoryProjectionDocumentStore<TReadModel, TKey>>());

        return services;
    }

    public static IServiceCollection AddInMemoryGraphProjectionStore(
        this IServiceCollection services)
    {
        services.AddSingleton<IProjectionGraphStore, InMemoryProjectionGraphStore>();

        return services;
    }
}
