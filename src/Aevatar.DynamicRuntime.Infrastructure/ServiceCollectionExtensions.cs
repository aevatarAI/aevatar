using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Application;
using Aevatar.Foundation.Abstractions.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.DynamicRuntime.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDynamicRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryEventEnvelopeBusState>();
        services.TryAddSingleton<IDynamicRuntimeReadStore, InMemoryDynamicRuntimeReadStore>();
        services.TryAddSingleton<IStateStore<ScriptServiceDefinitionState>, InMemoryScriptServiceDefinitionStateStore>();
        services.TryAddSingleton<IIdempotencyPort, InMemoryIdempotencyPort>();
        services.TryAddSingleton<IConcurrencyTokenPort, InMemoryConcurrencyTokenPort>();
        services.TryAddSingleton<IImageReferenceResolver, DefaultImageReferenceResolver>();
        services.TryAddSingleton<IScriptComposeSpecValidator, DefaultScriptComposeSpecValidator>();
        services.TryAddSingleton<IScriptComposeReconcilePort, DefaultScriptComposeReconcilePort>();
        services.TryAddSingleton<IAgentBuildPlanPort, DefaultAgentBuildPlanPort>();
        services.TryAddSingleton<IAgentBuildPolicyPort, DefaultAgentBuildPolicyPort>();
        services.TryAddSingleton<IAgentBuildExecutionPort, DefaultAgentBuildExecutionPort>();
        services.TryAddSingleton<IServiceModePolicyPort, DefaultServiceModePolicyPort>();
        services.TryAddSingleton<IBuildApprovalPort, DefaultBuildApprovalPort>();
        services.TryAddSingleton<IScriptCompilationPolicy, DefaultScriptCompilationPolicy>();
        services.TryAddSingleton<IScriptAssemblyLoadPolicy, DefaultScriptAssemblyLoadPolicy>();
        services.TryAddSingleton<IScriptSandboxPolicy, DefaultScriptSandboxPolicy>();
        services.TryAddSingleton<IScriptResourceQuotaPolicy, DefaultScriptResourceQuotaPolicy>();
        services.TryAddSingleton<IScriptNetworkPolicy, DefaultScriptNetworkPolicy>();
        services.TryAddSingleton<IEventEnvelopePublisherPort, InMemoryEventEnvelopePublisherPort>();
        services.TryAddSingleton<IEventEnvelopeSubscriberPort, InMemoryEventEnvelopeSubscriberPort>();
        services.TryAddSingleton<IEventEnvelopeDedupPort, InMemoryEventEnvelopeDedupPort>();
        services.TryAddSingleton<IEventEnvelopeDeliveryPort, InMemoryEventEnvelopeDeliveryPort>();
        services.TryAddSingleton<IScriptSideEffectPlanner, ScriptSideEffectPlanner>();
        services.TryAddSingleton<IDynamicRuntimeEventProjector, DynamicRuntimeEventProjector>();
        services.TryAddSingleton<ScriptRoleAgentChatClient>();
        services.TryAddSingleton<IDynamicScriptExecutionService, RoslynDynamicScriptExecutionService>();
        services.TryAddSingleton<DynamicRuntimeApplicationService>();
        services.TryAddSingleton<IDynamicRuntimeCommandService>(sp => sp.GetRequiredService<DynamicRuntimeApplicationService>());
        services.TryAddSingleton<IDynamicRuntimeQueryService>(sp => sp.GetRequiredService<DynamicRuntimeApplicationService>());
        return services;
    }
}
