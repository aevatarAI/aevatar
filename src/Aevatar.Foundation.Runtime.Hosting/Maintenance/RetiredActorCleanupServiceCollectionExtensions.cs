using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Hosting.Maintenance;

// Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
public static class RetiredActorCleanupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the spec-driven retired-actor cleanup hosted service. Each retired
    /// module separately contributes one or more <see cref="Aevatar.Foundation.Abstractions.Maintenance.IRetiredActorSpec"/>
    /// via <c>TryAddEnumerable</c> in its own <c>Add*</c> DI extension.
    /// Startup only triggers cleanup; per-target revalidation makes duplicate
    /// runs converge without requiring startup ordering against module services.
    /// </summary>
    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    public static IServiceCollection AddRetiredActorCleanup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, RetiredActorCleanupHostedService>());
        return services;
    }
}
