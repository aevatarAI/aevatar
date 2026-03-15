namespace Aevatar.Scripting.Core.Ports;

public interface IScriptAuthorityReadModelActivationPort
{
    Task ActivateAsync(string actorId, CancellationToken ct);
}
