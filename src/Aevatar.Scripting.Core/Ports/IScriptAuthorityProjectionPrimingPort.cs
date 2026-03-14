namespace Aevatar.Scripting.Core.Ports;

public interface IScriptAuthorityProjectionPrimingPort
{
    Task PrimeAsync(string actorId, CancellationToken ct);
}
