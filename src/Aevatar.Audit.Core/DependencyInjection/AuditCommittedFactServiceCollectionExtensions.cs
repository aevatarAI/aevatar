using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Audit.Core.Projection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Audit.Core.DependencyInjection;

public static class AuditCommittedFactServiceCollectionExtensions
{
    public static IServiceCollection AddAuditCommittedFactCapture(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AuditCommittedEventTranslatorRegistry>();
        services.TryAddSingleton<IAuditTrailAppender, ProjectionAuditTrailAppender>();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        return services;
    }

    public static IServiceCollection AddAuditCommittedFactMaterializer<TContext>(this IServiceCollection services)
        where TContext : class, IProjectionMaterializationContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuditCommittedFactCapture();
        services.AddProjectionArtifactMaterializer<TContext, CommittedAuditArtifactMaterializer<TContext>>();
        return services;
    }
}
