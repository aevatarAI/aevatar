using Aevatar.Foundation.Abstractions;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class DefaultScriptingRuntimeQueryModes : IScriptingRuntimeQueryModes
{
    private const string OrleansRuntimeTypeName =
        "Aevatar.Foundation.Runtime.Implementations.Orleans.Actors.OrleansActorRuntime";

    public DefaultScriptingRuntimeQueryModes(
        IActorRuntime runtime,
        ScriptingRuntimeQueryModeOptions options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        UseEventDrivenDefinitionQuery = options.UseEventDrivenDefinitionQuery
            ?? IsOrleansRuntime(runtime);
    }

    public bool UseEventDrivenDefinitionQuery { get; }

    private static bool IsOrleansRuntime(IActorRuntime runtime)
    {
        var runtimeType = runtime.GetType().FullName ?? string.Empty;
        return string.Equals(runtimeType, OrleansRuntimeTypeName, StringComparison.Ordinal);
    }
}
