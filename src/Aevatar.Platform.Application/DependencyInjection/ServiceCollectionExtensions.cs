using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Queries;
using Aevatar.Platform.Application.Commands;
using Aevatar.Platform.Application.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Platform.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformApplication(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformCommandApplicationService, PlatformCommandApplicationService>();
        services.AddSingleton<IPlatformCommandQueryApplicationService, PlatformCommandQueryApplicationService>();
        services.AddSingleton<IPlatformAgentQueryApplicationService, PlatformAgentQueryApplicationService>();
        return services;
    }
}
