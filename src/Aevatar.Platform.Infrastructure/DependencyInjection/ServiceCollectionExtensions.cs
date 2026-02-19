using Aevatar.CQRS.Runtime.Abstractions.Dispatch;
using Aevatar.Platform.Abstractions.Catalog;
using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;
using Aevatar.Platform.Application.DependencyInjection;
using Aevatar.Platform.Infrastructure.Catalog;
using Aevatar.Platform.Infrastructure.Dispatch;
using Aevatar.Platform.Infrastructure.Store;
using Aevatar.Platform.Sagas.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Platform.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformSubsystem(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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
        services.AddPlatformCommandSagas();

        services.AddPlatformApplication();
        return services;
    }
}
