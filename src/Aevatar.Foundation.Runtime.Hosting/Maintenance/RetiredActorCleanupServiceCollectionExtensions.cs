using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Maintenance;

// Refactor (issue1287-first):
//   Old pattern: DI registered startup cleanup that completed through EventStore marker coordination.
//   New principle: DI registers one BackgroundService trigger; target safety is owned by revalidation.
public static class RetiredActorCleanupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the spec-driven retired-actor cleanup hosted service. Each retired
    /// module separately contributes one or more <see cref="Aevatar.Foundation.Abstractions.Maintenance.IRetiredActorSpec"/>
    /// via <c>TryAddEnumerable</c> in its own <c>Add*</c> DI extension.
    /// Startup only triggers cleanup; per-target revalidation makes duplicate
    /// runs converge without requiring startup ordering against module services.
    /// </summary>
    // Refactor (issue1287-first):
    //   Old pattern: registration implied a startup cleanup owner coordinated by marker state.
    //   New principle: registration only wires the BackgroundService trigger; target safety is revalidated.
    public static IServiceCollection AddRetiredActorCleanup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RetiredActorCleanupHostedService>());
        return services;
    }
}
