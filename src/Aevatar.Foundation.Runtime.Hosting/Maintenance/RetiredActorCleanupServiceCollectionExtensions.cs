using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Maintenance;

public static class RetiredActorCleanupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the spec-driven retired-actor cleanup hosted service. Each retired
    /// module separately contributes one or more <see cref="Aevatar.Foundation.Abstractions.Maintenance.IRetiredActorSpec"/>
    /// via <c>TryAddEnumerable</c> in its own <c>Add*</c> DI extension.
    /// Call this before module DI extensions whose startup services activate the
    /// retired actors.
    /// </summary>
    public static IServiceCollection AddRetiredActorCleanup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RetiredActorCleanupHostedService>());
        return services;
    }
}
