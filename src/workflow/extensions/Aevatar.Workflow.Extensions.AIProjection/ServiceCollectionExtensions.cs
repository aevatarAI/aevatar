using Aevatar.AI.Projection.DependencyInjection;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.AIProjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowAIProjectionExtensions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddAIDefaultProjectionLayer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>();
    }
}
