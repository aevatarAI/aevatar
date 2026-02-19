using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Hosting;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Runtime;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Storage;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Stores;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Timeouts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.CQRS.Sagas.Runtime.FileSystem.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCqrsSagasFileSystem(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _ = configuration;

        services.TryAddSingleton<SagaPathResolver>();
        services.TryAddSingleton<FileSystemSagaTimeoutStore>();
        services.TryAddSingleton<ISagaRepository, FileSystemSagaRepository>();
        services.TryAddSingleton<ISagaTimeoutScheduler, FileSystemSagaTimeoutScheduler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, SagaTimeoutDispatchHostedService>());

        return services;
    }
}
