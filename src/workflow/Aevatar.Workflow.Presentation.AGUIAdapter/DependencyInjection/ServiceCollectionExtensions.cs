using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Presentation.AGUIAdapter.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowExecutionAGUIAdapter(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventEnvelopeToWorkflowRunEventMapper, EventEnvelopeToWorkflowRunEventMapper>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, WorkflowRunExecutionStartedEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, StartWorkflowRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, StepRequestRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, StepCompletedRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, AITextStreamRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, AIReasoningRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, WorkflowCompletedRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, WorkflowStoppedRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, ToolCallRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, WorkflowSuspendedRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, WorkflowWaitingSignalRunEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, WorkflowSignalBufferedRunEventEnvelopeMappingHandler>());

        return services;
    }
}
