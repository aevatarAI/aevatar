namespace Aevatar.CQRS.Runtime.Abstractions.Persistence;

public interface IOutboxStore
{
    Task AppendAsync(OutboxMessage message, CancellationToken ct = default);

    Task<IReadOnlyList<OutboxMessage>> ListPendingAsync(int take = 100, CancellationToken ct = default);

    Task MarkDispatchedAsync(string messageId, CancellationToken ct = default);
}
