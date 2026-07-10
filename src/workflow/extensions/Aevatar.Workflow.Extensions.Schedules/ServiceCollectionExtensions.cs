using Aevatar.Workflow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Schedules;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowScheduleExtensions(this IServiceCollection services)
    {
        return services.AddWorkflowModulePack<WorkflowScheduleModulePack>();
    }
}
