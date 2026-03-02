using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class DefaultScriptingRuntimeQueryModes : IScriptingRuntimeQueryModes
{
    public DefaultScriptingRuntimeQueryModes(IActorRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var assemblyName = runtime.GetType().Assembly.GetName().Name ?? string.Empty;
        UseEventDrivenDefinitionQuery = assemblyName.Contains(".Orleans", StringComparison.Ordinal);
    }

    public bool UseEventDrivenDefinitionQuery { get; }
}
