using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Ports;

public interface IScriptRuntimeCommandPort
{
    Task<string> SpawnRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct);

    Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct);
}
