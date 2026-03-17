namespace Aevatar.Scripting.Core.Ports;

public interface IScriptEvolutionReadModelActivationPort
{
    Task<bool> ActivateAsync(string actorId, CancellationToken ct = default);
}
