using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Maintenance;

// Refactor (issue1056/impl): Old pattern: hosted-service EventStore marker replay/write. New principle: actor-owned cleanup lease via IActorDispatchPort + EventEnvelope + narrow command-result contract (Phase 9 r6 consensus).
public interface IRetiredActorCleanupCoordinatorResultPort
{
    string CreateResultStreamId(string commandId);

    Task<T> AwaitResultAsync<T>(string commandId, string resultStreamId, CancellationToken ct)
        where T : class, IMessage<T>, new();

    Task PublishAsync(
        string resultStreamId,
        RetiredActorCleanupCoordinatorCommandResult result,
        CancellationToken ct = default);
}
