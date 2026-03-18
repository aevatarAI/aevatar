namespace Aevatar.Scripting.Core.Ports;

public interface IScriptExecutionReadModelActivationPort
{
    Task<bool> ActivateAsync(string actorId, CancellationToken ct = default);
}
