using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Orchestration;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowApplication(
        this IServiceCollection services,
        Action<WorkflowDefinitionRegistryOptions>? configureRegistry = null)
    {
        var options = new WorkflowDefinitionRegistryOptions();
        configureRegistry?.Invoke(options);

        services.AddSingleton<IWorkflowDefinitionRegistry>(_ =>
        {
            var registry = new WorkflowDefinitionRegistry();
            if (options.RegisterBuiltInDirectWorkflow)
                registry.Register("direct", WorkflowDefinitionRegistry.BuiltInDirectYaml);

            return registry;
        });

        services.AddSingleton<IWorkflowExecutionTopologyResolver, ActorRuntimeWorkflowExecutionTopologyResolver>();
        services.AddSingleton<IWorkflowExecutionRunOrchestrator, WorkflowExecutionRunOrchestrator>();
        services.AddSingleton<IWorkflowExecutionReportMapper, WorkflowExecutionReportMapper>();
        services.AddSingleton<IWorkflowRunActorResolver, WorkflowRunActorResolver>();
        services.AddSingleton<IWorkflowChatRequestEnvelopeFactory, WorkflowChatRequestEnvelopeFactory>();
        services.AddSingleton<IWorkflowRunRequestExecutor, WorkflowRunRequestExecutor>();
        services.AddSingleton<IWorkflowRunOutputStreamer, WorkflowRunOutputStreamer>();
        services.TryAddSingleton<IWorkflowExecutionReportArtifactSink, NoopWorkflowExecutionReportArtifactSink>();
        services.AddSingleton<IWorkflowChatRunApplicationService, WorkflowChatRunApplicationService>();
        services.AddSingleton<IWorkflowExecutionQueryApplicationService, WorkflowExecutionQueryApplicationService>();
        return services;
    }
}
