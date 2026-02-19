using Aevatar.CQRS.Sagas.Abstractions.Configuration;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.CQRS.Sagas.Core.Dispatch;
using Aevatar.CQRS.Sagas.Core.Hosting;
using Aevatar.CQRS.Sagas.Core.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.CQRS.Sagas.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCqrsSagasCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SagaRuntimeOptions>()
            .Bind(configuration.GetSection("Cqrs:Sagas"));

        services.PostConfigure<SagaRuntimeOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
                options.WorkingDirectory = Path.Combine("artifacts", "cqrs", "sagas");

            if (options.ActorScanIntervalMs <= 0)
                options.ActorScanIntervalMs = 1000;

            if (options.MaxActionsPerEvent <= 0)
                options.MaxActionsPerEvent = 256;

            if (options.ConcurrencyRetryAttempts <= 0)
                options.ConcurrencyRetryAttempts = 5;

            if (options.TimeoutDispatchIntervalMs <= 0)
                options.TimeoutDispatchIntervalMs = 500;

            if (options.TimeoutDispatchBatchSize <= 0)
                options.TimeoutDispatchBatchSize = 128;
        });

        services.TryAddSingleton<ISagaCorrelationResolver, DefaultSagaCorrelationResolver>();
        services.TryAddSingleton<ISagaCommandEmitter, CommandBusSagaCommandEmitter>();
        services.TryAddSingleton<ISagaTimeoutScheduler, NoopSagaTimeoutScheduler>();
        services.TryAddSingleton<ISagaRuntime, SagaRuntime>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ActorSagaSubscriptionHostedService>());

        return services;
    }

    public static IServiceCollection AddSaga<TSaga>(this IServiceCollection services)
        where TSaga : class, ISaga
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISaga, TSaga>());
        return services;
    }
}
