namespace Aevatar.Scripting.Core.Runtime;

public interface IScriptBehaviorDispatcher
{
    Task<IReadOnlyList<ScriptDomainFactCommitted>> DispatchAsync(
        ScriptBehaviorDispatchRequest request,
        CancellationToken ct);
}
