using Aevatar.Scripting.Application.AI;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Schema;
using Aevatar.Scripting.Hosting.Ports;
using Aevatar.Scripting.Infrastructure.Compilation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Scripting.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScriptCapability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ScriptSandboxPolicy>();
        services.TryAddSingleton<IScriptExecutionEngine, RoslynScriptExecutionEngine>();
        services.TryAddSingleton<IScriptPackageCompiler, RoslynScriptPackageCompiler>();
        services.TryAddSingleton<IScriptReadModelSchemaActivationPolicy, DefaultScriptReadModelSchemaActivationPolicy>();
        services.TryAddSingleton<IScriptEvolutionApplicationService, ScriptEvolutionApplicationService>();
        services.TryAddSingleton<IScriptRuntimeCapabilityComposer, ScriptRuntimeCapabilityComposer>();
        services.TryAddSingleton<IScriptRuntimeExecutionOrchestrator, ScriptRuntimeExecutionOrchestrator>();
        services.TryAddSingleton<IScriptDefinitionSnapshotPort, RuntimeScriptDefinitionSnapshotPort>();
        services.TryAddSingleton<IScriptDefinitionLifecyclePort, RuntimeScriptDefinitionLifecyclePort>();
        services.TryAddSingleton<IScriptRuntimeLifecyclePort, RuntimeScriptRuntimeLifecyclePort>();
        services.TryAddSingleton<IScriptCatalogPort, RuntimeScriptCatalogPort>();
        services.TryAddSingleton<IScriptPolicyGatePort, RuntimeScriptPolicyGatePort>();
        services.TryAddSingleton<IScriptValidationPipelinePort, RuntimeScriptValidationPipelinePort>();
        services.TryAddSingleton<IScriptPromotionPort, RuntimeScriptPromotionPort>();
        services.TryAddSingleton<IScriptEvolutionPort, RuntimeScriptEvolutionPort>();
        services.TryAddSingleton<IGAgentEventRoutingPort, RuntimeGAgentEventRoutingPort>();
        services.TryAddSingleton<IGAgentInvocationPort, RuntimeGAgentInvocationPort>();
        services.TryAddSingleton<IGAgentFactoryPort, RuntimeGAgentFactoryPort>();
        services.TryAddSingleton<IAICapability>(sp =>
        {
            var roleAgentPort = sp.GetService<IRoleAgentPort>();
            return roleAgentPort == null
                ? new NoopAICapability()
                : new RoleAgentDelegateAICapability(roleAgentPort);
        });

        return services;
    }
}
