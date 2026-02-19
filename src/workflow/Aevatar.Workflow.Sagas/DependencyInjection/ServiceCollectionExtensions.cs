using Aevatar.Workflow.Sagas.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Sagas.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowExecutionSagas(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISaga, WorkflowExecutionSaga>());
        services.TryAddSingleton<IWorkflowExecutionSagaQueryService, WorkflowExecutionSagaQueryService>();
        return services;
    }
}
