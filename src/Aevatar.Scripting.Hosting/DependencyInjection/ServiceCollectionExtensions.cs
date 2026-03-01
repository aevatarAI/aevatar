using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Scripting.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScriptCapability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ScriptSandboxPolicy>();
        services.TryAddSingleton<IScriptAgentCompiler, RoslynScriptAgentCompiler>();
        services.TryAddSingleton<IAICapability>(sp =>
            new RoleAgentDelegateAICapability(sp.GetRequiredService<IRoleAgentPort>()));

        return services;
    }
}
