namespace Aevatar.Scripting.Core.Ports;

public interface IScriptRuntimeProvisioningPort
{
    Task<string> EnsureRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct);
}
