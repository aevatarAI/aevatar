using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Adapters;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Orchestration;
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

        services.AddSingleton<IWorkflowRunActorResolver, WorkflowRunActorResolver>();
        services.AddSingleton<ICommandEnvelopeFactory<WorkflowChatRunRequest>, WorkflowChatRequestEnvelopeFactory>();
        services.AddSingleton<IWorkflowRunRequestExecutor, WorkflowRunRequestExecutor>();
        services.AddSingleton<WorkflowRunOutputStreamer>();
        services.AddSingleton<IWorkflowRunOutputStreamer>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IEventOutputStream<WorkflowRunEvent, WorkflowOutputFrame>>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IEventFrameMapper<WorkflowRunEvent, WorkflowOutputFrame>>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IWorkflowExecutionReportArtifactSink, NoopWorkflowExecutionReportArtifactSink>();
        services.TryAddSingleton<IWorkflowExecutionTopologyResolver, ActorRuntimeWorkflowExecutionTopologyResolver>();
        services.AddSingleton<IWorkflowRunCommandService, WorkflowChatRunApplicationService>();
        services.AddSingleton<IWorkflowExecutionQueryApplicationService, WorkflowExecutionQueryApplicationService>();
        services.TryAddSingleton<WorkflowCommandExecutionServiceAdapter>();
        services.TryAddSingleton<ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>>(sp =>
            sp.GetRequiredService<WorkflowCommandExecutionServiceAdapter>());
        return services;
    }
}
