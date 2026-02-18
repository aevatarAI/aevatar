using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.Abstractions.Configuration;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Aevatar.CQRS.Runtime.Abstractions.Persistence;
using Aevatar.CQRS.Runtime.Abstractions.Serialization;
using Aevatar.CQRS.Runtime.FileSystem.Dispatch;
using Aevatar.CQRS.Runtime.FileSystem.Serialization;
using Aevatar.CQRS.Runtime.FileSystem.Storage;
using Aevatar.CQRS.Runtime.FileSystem.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Runtime.FileSystem.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCqrsRuntimeFileSystemCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CqrsRuntimeOptions>()
            .Bind(configuration.GetSection("Cqrs"));
        services.PostConfigure<CqrsRuntimeOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.Runtime))
                options.Runtime = "Wolverine";
            if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
                options.WorkingDirectory = Path.Combine("artifacts", "cqrs");
        });

        services.TryAddSingleton<CqrsPathResolver>();

        services.TryAddSingleton<ICommandStateStore, FileSystemCommandStateStore>();
        services.TryAddSingleton<IInboxStore, FileSystemInboxStore>();
        services.TryAddSingleton<IOutboxStore, FileSystemOutboxStore>();
        services.TryAddSingleton<IDeadLetterStore, FileSystemDeadLetterStore>();
        services.TryAddSingleton<IProjectionCheckpointStore, FileSystemProjectionCheckpointStore>();
        services.TryAddSingleton<ICommandPayloadSerializer, JsonCommandPayloadSerializer>();
        services.TryAddSingleton<ICommandDispatcher, ServiceProviderCommandDispatcher>();
        services.TryAddSingleton<IQueuedCommandExecutor, QueuedCommandExecutor>();

        return services;
    }
}
