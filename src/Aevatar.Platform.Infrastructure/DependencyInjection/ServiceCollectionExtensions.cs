using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Aevatar.CQRS.Runtime.FileSystem.DependencyInjection;
using Aevatar.CQRS.Runtime.Implementations.MassTransit.DependencyInjection;
using Aevatar.CQRS.Runtime.Implementations.Wolverine.DependencyInjection;
using Aevatar.Platform.Abstractions.Catalog;
using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;
using Aevatar.Platform.Application.DependencyInjection;
using Aevatar.Platform.Infrastructure.Catalog;
using Aevatar.Platform.Infrastructure.Dispatch;
using Aevatar.Platform.Infrastructure.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Platform.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformSubsystem(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddCqrsCore();
        services.AddCqrsRuntimeFileSystemCore(configuration);
        var runtime = configuration["Cqrs:Runtime"] ?? "Wolverine";
        if (string.Equals(runtime, "MassTransit", StringComparison.OrdinalIgnoreCase))
            services.AddCqrsRuntimeMassTransit();
        else
            services.AddCqrsRuntimeWolverine();

        services.AddHttpClient();
        services.AddOptions<SubsystemEndpointOptions>()
            .Bind(configuration.GetSection("SubsystemEndpoints"));

        services.AddSingleton<BuiltInAgentCatalog>();
        services.AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<BuiltInAgentCatalog>());
        services.AddSingleton<IAgentCommandRouter>(sp => sp.GetRequiredService<BuiltInAgentCatalog>());
        services.AddSingleton<IAgentQueryRouter>(sp => sp.GetRequiredService<BuiltInAgentCatalog>());

        services.AddSingleton<IPlatformCommandStateStore, FileSystemPlatformCommandStateStore>();
        services.AddSingleton<IPlatformCommandDispatchGateway, HttpPlatformCommandDispatchGateway>();
        services.AddSingleton<ICommandHandler<PlatformDispatchCommand>, PlatformDispatchCommandHandler>();

        services.AddPlatformApplication();
        return services;
    }
}
