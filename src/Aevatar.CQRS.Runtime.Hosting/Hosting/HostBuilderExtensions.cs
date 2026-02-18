using Aevatar.CQRS.Runtime.Implementations.Wolverine.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Aevatar.CQRS.Runtime.Hosting.Hosting;

public static class HostBuilderExtensions
{
    public static IHostBuilder UseAevatarCqrsRuntime(
        this IHostBuilder hostBuilder,
        IConfiguration configuration)
    {
        var runtime = configuration["Cqrs:Runtime"] ?? "Wolverine";
        if (string.Equals(runtime, "MassTransit", StringComparison.OrdinalIgnoreCase))
            return hostBuilder;

        return hostBuilder.UseAevatarCqrsWolverine();
    }
}
