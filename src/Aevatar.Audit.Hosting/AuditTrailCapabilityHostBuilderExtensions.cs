using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Audit.Hosting;

public static class AuditTrailCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddAuditTrailCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<AuditTrailEndpointDependencies>();
        builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
        {
            Name = "audit-trail",
            Category = "capability",
            RequiredRoutes =
            [
                "/api/audit/trail",
                "/api/audit/chat-activity",
                "/api/audit/trail/cloudevents",
                "/api/audit/actor-resolutions",
            ],
            Critical = false,
            ProbeAsync = static async (serviceProvider, ct) =>
            {
                var queryPort = serviceProvider.GetService<IAuditTrailQueryPort>();
                if (queryPort is null)
                    return AevatarHealthContributorResult.Degraded("Audit trail query port is not configured.");

                var from = DateTimeOffset.UtcNow.AddDays(1);
                try
                {
                    _ = await queryPort.QueryAsync(
                        new AuditTrailQuery
                        {
                            OccurredFrom = from,
                            OccurredTo = from.AddMinutes(1),
                            Take = 1,
                        },
                        ct);
                    return AevatarHealthContributorResult.Healthy("Audit trail query/index is available.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return AevatarHealthContributorResult.Unhealthy(
                        "Audit trail query/index is unavailable.",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["exceptionType"] = exception.GetType().Name,
                        });
                }
            },
        });

        return builder.AddAevatarCapability(
            "audit-trail",
            configureServices: static (_, _) => { },
            mapEndpoints: static app => app.MapAuditTrailEndpoints());
    }
}
