using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Credentials;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Abstractions.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Hosting.DependencyInjection;

public static class NyxIdAuthorizationCatalogHostingServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdAuthorizationCatalogHosting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(NyxIdAuthorizationCatalogHostingRegistrationsMarker)))
        {
            return services;
        }

        services.AddAevatarAgentKindRegistry(builder =>
            builder.Register<NyxIdAuthorizationCatalogGAgent>());
        services.AddScheduledInvocationAuthorization();
        services.AddNyxIdApiAccess(configuration);
        services.TryAddSingleton<
            IWorkflowCallerAccessTokenProvider,
            NyxIdWorkflowCallerAccessTokenProvider>();
        services.Replace(ServiceDescriptor.Singleton<
            INyxIdScheduledOperationAuthorizationPort,
            NyxIdApprovalPolicyScheduledOperationAuthorizationPort>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<
            IExternalWorkflowCapabilitySource,
            NyxIdExternalWorkflowCapabilitySource>());
        services.AddGAgentServiceProjection();
        services.AddGAgentServiceProjectionReadModelProviders(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<INyxIdAuthorizationCatalogCommandPort, NyxIdAuthorizationCatalogCommandPort>();
        services.TryAddSingleton<INyxIdAuthorizationCatalogRefreshPort, NyxIdAuthorizationCatalogRefreshPort>();
        services.TryAddTransient<NyxIdAuthorizationCatalogGAgent>();
        services.AddSingleton<NyxIdAuthorizationCatalogHostingRegistrationsMarker>();
        return services;
    }
}

internal sealed class NyxIdAuthorizationCatalogHostingRegistrationsMarker;
