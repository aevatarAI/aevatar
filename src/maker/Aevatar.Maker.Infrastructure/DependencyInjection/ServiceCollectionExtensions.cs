using Aevatar.Maker.Application.DependencyInjection;
using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Maker.Core;
using Aevatar.Maker.Infrastructure.Runs;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Presentation.AGUIAdapter.DependencyInjection;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Maker.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMakerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAevatarWorkflow();
        services.AddWorkflowExecutionProjectionCQRS(options =>
            configuration.GetSection("WorkflowExecutionProjection").Bind(options));
        services.AddWorkflowExecutionAGUIAdapter();
        services.AddWorkflowExecutionProjectionProjector<WorkflowExecutionAGUIEventProjector>();
        services.AddAevatarMakerCore();
        services.AddSingleton<IMakerRunExecutionPort, WorkflowMakerRunExecutionPort>();
        services.AddMakerApplication();
        return services;
    }

    public static IServiceCollection AddMakerCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddMakerInfrastructure(configuration);
    }
}
