using Aevatar.AppPlatform.Application.DependencyInjection;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Infrastructure.DependencyInjection;
using Aevatar.AppPlatform.Hosting.Invocation;
using Aevatar.AppPlatform.Hosting.OpenApi;
using Aevatar.AppPlatform.Hosting.Serialization;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.AppPlatform.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppPlatformCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAppPlatformInfrastructure(configuration);
        services.AddAppPlatformApplication();
        services.AddSingleton<IAppFunctionRuntimeInvocationPort>(sp =>
            new WorkflowAppFunctionRuntimeInvocationPort(
                sp.GetService<ICommandInteractionService<
                    WorkflowChatRunRequest,
                    WorkflowChatRunAcceptedReceipt,
                    WorkflowChatRunStartError,
                    WorkflowRunEventEnvelope,
                    WorkflowProjectionCompletionStatus>>(),
                sp.GetRequiredService<IOperationCommandPort>(),
                sp.GetRequiredService<ILogger<WorkflowAppFunctionRuntimeInvocationPort>>(),
                sp.GetService<IHostApplicationLifetime>()));
        services.AddSingleton<IAppOpenApiDocumentPort, AppOpenApiDocumentPort>();
        services.AddSingleton<IAppFunctionInvokeRequestSerializer, AppFunctionInvokeRequestSerializer>();
        return services;
    }
}
