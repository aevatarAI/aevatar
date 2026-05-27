using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: Workflow.Core directly knew the AI provider implementation. New principle: Integration.AI wires provider adapters behind workflow-owned abstractions.
public static class WorkflowAiDependencyInjection
{
    public static IServiceCollection AddWorkflowAiIntegration(this IServiceCollection services)
    {
        services.TryAddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddWorkflowModulePack<WorkflowAiModulePack>();
        services.TryAddSingleton<IWorkflowLlmInvocationPort, WorkflowAiLlmInvocationPort>();
        services.TryAddSingleton<IWorkflowRoleActorTypeResolver, WorkflowAiRoleActorTypeResolver>();
        return services;
    }
}
