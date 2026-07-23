using System.Globalization;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class GAgentServiceCapabilityHostBuilderExtensions
{
    private const int MaxProfileHealthDetails = 8;

    public static WebApplicationBuilder AddGAgentServiceCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Scope script routes and the script query probe exist only when the host composed
        // the scripting capability before this bundle (see AddGAgentServiceCapability).
        var scriptingCapabilityRegistered = builder.Services.Any(x =>
            x.ServiceType == typeof(Aevatar.Scripting.Hosting.DependencyInjection.ServiceCollectionExtensions.ScriptCapabilityRegistrationsMarker));
        string[] requiredRoutes = scriptingCapabilityRegistered
            ?
            [
                "/api/services",
                "/api/scopes/{scopeId}/binding",
                "/api/scopes/{scopeId}/workflows",
                "/api/scopes/{scopeId}/scripts",
                "/api/schedules",
                "/api/schedules/{scheduleId}",
                "/api/scopes/{scopeId}/agent-profiles",
                "/api/agent-profiles/{ownerHandle}/{profileSlug}",
            ]
            :
            [
                "/api/services",
                "/api/scopes/{scopeId}/binding",
                "/api/scopes/{scopeId}/workflows",
                "/api/schedules",
                "/api/schedules/{scheduleId}",
                "/api/scopes/{scopeId}/agent-profiles",
                "/api/agent-profiles/{ownerHandle}/{profileSlug}",
            ];

        builder.Services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
        {
            Name = "gagent-service",
            Category = "capability",
            RequiredRoutes = requiredRoutes,
            ProbeAsync = static async (serviceProvider, cancellationToken) =>
            {
                var lifecycleQueryPort = serviceProvider.GetRequiredService<IServiceLifecycleQueryPort>();
                _ = await lifecycleQueryPort.ListServicesAsync(string.Empty, string.Empty, string.Empty, 1, cancellationToken);

                var scopeWorkflowQueryPort = serviceProvider.GetRequiredService<IScopeWorkflowQueryPort>();
                _ = await scopeWorkflowQueryPort.ListAsync("health", cancellationToken);

                if (serviceProvider.GetService<IScopeScriptQueryPort>() is { } scopeScriptQueryPort)
                    _ = await scopeScriptQueryPort.ListAsync("health", cancellationToken);

                var profileReadiness = serviceProvider
                    .GetRequiredService<ISystemAgentProfileReadinessService>();
                var profileStatus = await profileReadiness.GetAsync(cancellationToken);
                AgentProfileTelemetry.RecordRequiredSystemReadiness(
                    profileStatus.IsReady ? "ready" : "not_ready");
                if (!profileStatus.IsReady)
                {
                    return AevatarHealthContributorResult.Unhealthy(
                        "Required system Agent Profiles are not execution-visible.",
                        BuildProfileHealthDetails(profileStatus));
                }

                return AevatarHealthContributorResult.Healthy("GAgent service capability is ready.");
            },
        });

        return builder.AddAevatarCapability(
            "gagent-service",
            static (services, configuration) => services.AddGAgentServiceCapability(configuration),
            static app => app.MapGAgentServiceEndpoints());
    }

    private static IReadOnlyDictionary<string, string> BuildProfileHealthDetails(
        SystemAgentProfileReadinessSnapshot snapshot)
    {
        var requiredProfiles = snapshot.Profiles
            .Where(static profile => profile.Required)
            .ToArray();
        var nonReadyProfiles = requiredProfiles
            .Where(static profile => profile.Status != SystemAgentProfileReadinessStatus.Ready)
            .ToArray();
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["required_profile_count"] = requiredProfiles.Length.ToString(CultureInfo.InvariantCulture),
            ["non_ready_required_profile_count"] =
                nonReadyProfiles.Length.ToString(CultureInfo.InvariantCulture),
        };
        for (var index = 0; index < Math.Min(nonReadyProfiles.Length, MaxProfileHealthDetails); index++)
        {
            var profile = nonReadyProfiles[index];
            var prefix = $"profile_{index}";
            details[$"{prefix}_reference"] =
                $"{profile.Reference.OwnerHandle}/{profile.Reference.ProfileSlug}";
            details[$"{prefix}_status"] = ReadinessStatus(profile.Status);
            details[$"{prefix}_reason"] = ReadinessReason(profile.Reason);
        }

        return details;
    }

    private static string ReadinessStatus(SystemAgentProfileReadinessStatus status) => status switch
    {
        SystemAgentProfileReadinessStatus.Ready => "ready",
        SystemAgentProfileReadinessStatus.Pending => "pending",
        SystemAgentProfileReadinessStatus.Unavailable => "unavailable",
        SystemAgentProfileReadinessStatus.Unhealthy => "unhealthy",
        _ => "unspecified",
    };

    private static string ReadinessReason(SystemAgentProfileReadinessReason reason) => reason switch
    {
        SystemAgentProfileReadinessReason.None => "none",
        SystemAgentProfileReadinessReason.NamespaceMissing => "namespace_missing",
        SystemAgentProfileReadinessReason.NamespaceProvisioning => "namespace_provisioning",
        SystemAgentProfileReadinessReason.NamespaceProvisioningFailed => "namespace_provisioning_failed",
        SystemAgentProfileReadinessReason.NamespaceConflict => "namespace_conflict",
        SystemAgentProfileReadinessReason.ManagementSnapshotMissing => "management_snapshot_missing",
        SystemAgentProfileReadinessReason.ProfileIdentityConflict => "profile_identity_conflict",
        SystemAgentProfileReadinessReason.DraftDrift => "draft_drift",
        SystemAgentProfileReadinessReason.PublicationPending => "publication_pending",
        SystemAgentProfileReadinessReason.OrnnAccessTokenUnavailable => "ornn_access_token_unavailable",
        SystemAgentProfileReadinessReason.ExecutionSnapshotMissing => "execution_snapshot_missing",
        SystemAgentProfileReadinessReason.ExecutionSnapshotLagging => "execution_snapshot_lagging",
        _ => "unspecified",
    };
}
