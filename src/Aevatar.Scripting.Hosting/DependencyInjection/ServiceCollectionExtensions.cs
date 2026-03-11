using Aevatar.Scripting.Application.AI;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Schema;
using Aevatar.Scripting.Infrastructure.Ports;
using Aevatar.Scripting.Infrastructure.Compilation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Aevatar.Scripting.Projection.DependencyInjection;

namespace Aevatar.Scripting.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScriptCapability(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var useEventDrivenDefinitionQuery = ResolveUseEventDrivenDefinitionQuery(configuration);
        services.TryAddSingleton(new ScriptingRuntimeQueryModeOptions
        {
            UseEventDrivenDefinitionQuery = useEventDrivenDefinitionQuery,
        });

        services.TryAddSingleton<ScriptSandboxPolicy>();
        services.TryAddSingleton<IScriptingActorAddressResolver, DefaultScriptingActorAddressResolver>();
        services.TryAddSingleton<IScriptExecutionEngine, RoslynScriptExecutionEngine>();
        services.TryAddSingleton<IScriptPackageCompiler, RoslynScriptPackageCompiler>();
        services.TryAddSingleton<IScriptReadModelSchemaActivationPolicy, DefaultScriptReadModelSchemaActivationPolicy>();
        services.TryAddSingleton<IScriptEvolutionApplicationService, ScriptEvolutionApplicationService>();
        services.TryAddSingleton<IScriptRuntimeCapabilityComposer, ScriptRuntimeCapabilityComposer>();
        services.TryAddSingleton<IScriptRuntimeExecutionOrchestrator, ScriptRuntimeExecutionOrchestrator>();
        services.TryAddSingleton<IScriptingPortTimeouts, DefaultScriptingPortTimeouts>();
        services.TryAddSingleton<IScriptingRuntimeQueryModes, DefaultScriptingRuntimeQueryModes>();
        services.TryAddSingleton<IScriptEvolutionDecisionFallbackPort, RuntimeScriptEvolutionDecisionFallbackPort>();
        services.TryAddSingleton<RuntimeScriptActorAccessor>();
        services.TryAddSingleton<RuntimeScriptQueryClient>();
        services.TryAddSingleton<RuntimeScriptEvolutionLifecycleService>();
        services.TryAddSingleton<RuntimeScriptDefinitionLifecycleService>();
        services.TryAddSingleton<RuntimeScriptExecutionLifecycleService>();
        services.TryAddSingleton<RuntimeScriptCatalogLifecycleService>();
        services.TryAddSingleton<IScriptDefinitionSnapshotPort, RuntimeScriptDefinitionSnapshotPort>();
        services.TryAddSingleton<IScriptEvolutionProposalPort>(sp => sp.GetRequiredService<RuntimeScriptEvolutionLifecycleService>());
        services.TryAddSingleton<IScriptDefinitionCommandPort>(sp => sp.GetRequiredService<RuntimeScriptDefinitionLifecycleService>());
        services.TryAddSingleton<IScriptRuntimeCommandPort>(sp => sp.GetRequiredService<RuntimeScriptExecutionLifecycleService>());
        services.TryAddSingleton<IScriptCatalogCommandPort>(sp => sp.GetRequiredService<RuntimeScriptCatalogLifecycleService>());
        services.TryAddSingleton<IScriptCatalogQueryPort>(sp => sp.GetRequiredService<RuntimeScriptCatalogLifecycleService>());
        services.TryAddSingleton<IScriptEvolutionFlowPort, RuntimeScriptEvolutionFlowPort>();
        services.TryAddSingleton<IGAgentRuntimePort, RuntimeGAgentRuntimePort>();
        services.AddScriptingProjectionComponents();
        services.TryAddSingleton<IAICapability>(sp =>
        {
            var roleAgentPort = sp.GetService<IRoleAgentPort>();
            return roleAgentPort == null
                ? new NoopAICapability()
                : new RoleAgentDelegateAICapability(roleAgentPort);
        });

        return services;
    }

    private static bool? ResolveUseEventDrivenDefinitionQuery(IConfiguration? configuration)
    {
        var raw = configuration?["Scripting:Runtime:UseEventDrivenDefinitionQuery"];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!bool.TryParse(raw, out var parsed))
            throw new InvalidOperationException(
                $"Invalid boolean value '{raw}' for Scripting:Runtime:UseEventDrivenDefinitionQuery.");

        return parsed;
    }
}
