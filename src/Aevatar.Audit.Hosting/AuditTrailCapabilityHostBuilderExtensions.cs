using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Audit.Hosting;

public static class AuditTrailCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddAuditTrailCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
        {
            Name = "audit-trail",
            Category = "capability",
            RequiredRoutes =
            [
                "/api/audit/trail",
                "/api/audit/actor-resolutions",
            ],
            Critical = false,
            ProbeAsync = static (serviceProvider, _) =>
            {
                var configured = serviceProvider.GetService<IAuditTrailQueryPort>() is not null;
                return ValueTask.FromResult(configured
                    ? AevatarHealthContributorResult.Healthy("Audit trail query capability is configured.")
                    : AevatarHealthContributorResult.Degraded("Audit trail query port is not configured."));
            },
        });

        return builder.AddAevatarCapability(
            "audit-trail",
            configureServices: static (services, _) =>
            {
                services.TryAddSingleton<IAuditActorIdentityHasher, DefaultAuditActorIdentityHasher>();
            },
            mapEndpoints: static app => app.MapAuditTrailEndpoints());
    }
}
