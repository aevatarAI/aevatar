using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Scripting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register scripting agent tools. Port implementations (IScriptCompilationPort,
    /// IScriptSandboxExecutionPort, IScriptCatalogCommandPort, IScriptCatalogQueryPort)
    /// must be registered separately by the infrastructure layer.
    /// </summary>
    public static IServiceCollection AddScriptingTools(
        this IServiceCollection services,
        Action<ScriptingToolOptions>? configure = null)
    {
        var options = new ScriptingToolOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ScriptingAgentToolSource>());
        return services;
    }
}
