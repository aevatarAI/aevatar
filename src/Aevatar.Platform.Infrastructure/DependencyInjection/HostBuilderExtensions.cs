using Aevatar.CQRS.Runtime.Implementations.Wolverine.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Platform.Infrastructure.DependencyInjection;

public static class HostBuilderExtensions
{
    public static IHostBuilder UsePlatformCqrsRuntime(
        this IHostBuilder hostBuilder,
        IConfiguration configuration)
    {
        var runtime = configuration["Cqrs:Runtime"] ?? "Wolverine";
        if (string.Equals(runtime, "MassTransit", StringComparison.OrdinalIgnoreCase))
            return hostBuilder;

        return hostBuilder.UseAevatarCqrsWolverine();
    }
}
