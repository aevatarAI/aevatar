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
        Action<InMemoryWorkflowDefinitionCatalogOptions>? configureCatalog = null,
        Action<WorkflowRunBehaviorOptions>? configureRunBehavior = null)
    {
        var options = new InMemoryWorkflowDefinitionCatalogOptions();
        configureCatalog?.Invoke(options);
        var runBehaviorOptions = new WorkflowRunBehaviorOptions();
        configureRunBehavior?.Invoke(runBehaviorOptions);
        services.AddSingleton(options);
        services.AddSingleton(runBehaviorOptions);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowDefinitionSeedSource, BuiltInWorkflowDefinitionSeedSource>());
        services.AddSingleton<InMemoryWorkflowDefinitionCatalog>(sp =>
        {
            var catalog = new InMemoryWorkflowDefinitionCatalog();
            foreach (var seedSource in sp.GetServices<IWorkflowDefinitionSeedSource>())
            {
                foreach (var (name, yaml) in seedSource.GetSeedDefinitions())
                    catalog.Upsert(name, yaml);
            }

            return catalog;
        });
        services.AddSingleton<IWorkflowDefinitionCatalog>(sp => sp.GetRequiredService<InMemoryWorkflowDefinitionCatalog>());
        services.AddSingleton<IWorkflowDefinitionLookupService>(sp => sp.GetRequiredService<InMemoryWorkflowDefinitionCatalog>());

        services.AddSingleton<WorkflowDirectFallbackPolicy>();
        services.AddSingleton<IWorkflowRunActorResolver>(sp =>
            new WorkflowRunActorResolver(
                sp.GetRequiredService<IWorkflowRunActorPort>(),
                sp.GetRequiredService<IWorkflowDefinitionLookupService>(),
                sp.GetRequiredService<WorkflowRunBehaviorOptions>()));
        services.TryAddSingleton<ICommandContextPolicy, WorkflowCommandContextPolicy>();
        services.AddSingleton<ICommandEnvelopeFactory<WorkflowChatRunRequest>, WorkflowChatRequestEnvelopeFactory>();
        services.AddSingleton<IWorkflowRunRequestExecutor, WorkflowRunRequestExecutor>();
        services.AddSingleton<IWorkflowRunContextFactory, WorkflowRunContextFactory>();
        services.TryAddSingleton<IWorkflowRunStateSnapshotEmitter, WorkflowRunStateSnapshotEmitter>();
        services.AddSingleton<IWorkflowRunExecutionEngine, WorkflowRunExecutionEngine>();
        services.TryAddSingleton<IWorkflowRunCompletionPolicy, WorkflowRunCompletionPolicy>();
        services.TryAddSingleton<IWorkflowRunResourceFinalizer, WorkflowRunResourceFinalizer>();
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
