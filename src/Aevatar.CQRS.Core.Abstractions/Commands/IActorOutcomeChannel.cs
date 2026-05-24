namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface IActorOutcomeChannel<TOutcome>
    where TOutcome : Google.Protobuf.IMessage, new()
{
    Task<ActorOutcomeSubscription<TOutcome>> SubscribeAsync(
        string commandId,
        CancellationToken ct = default);

    Task PublishAsync(
        string commandId,
        TOutcome outcome,
        CancellationToken ct = default);
}
