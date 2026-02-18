using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Runtime.FileSystem.DependencyInjection;
using Aevatar.CQRS.Runtime.Implementations.MassTransit.DependencyInjection;
using Aevatar.CQRS.Runtime.Implementations.Wolverine.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Runtime.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarCqrsRuntime(
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

        return services;
    }
}
