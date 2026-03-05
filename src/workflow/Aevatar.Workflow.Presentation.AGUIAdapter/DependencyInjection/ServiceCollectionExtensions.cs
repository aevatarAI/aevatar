using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Presentation.AGUIAdapter.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowExecutionAGUIAdapter(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventEnvelopeToAGUIEventMapper, EventEnvelopeToAGUIEventMapper>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, StartWorkflowAGUIEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, StepRequestAGUIEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, StepCompletedAGUIEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, AITextStreamAGUIEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, AIReasoningAGUIEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, WorkflowCompletedAGUIEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, ToolCallAGUIEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, WorkflowSuspendedAGUIEventEnvelopeMappingHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, WorkflowWaitingSignalAGUIEventEnvelopeMappingHandler>());

        return services;
    }
}
