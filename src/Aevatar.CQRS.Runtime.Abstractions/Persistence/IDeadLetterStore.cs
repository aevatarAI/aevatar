namespace Aevatar.CQRS.Runtime.Abstractions.Persistence;

public interface IDeadLetterStore
{
    Task AppendAsync(DeadLetterMessage message, CancellationToken ct = default);

    Task<IReadOnlyList<DeadLetterMessage>> ListAsync(int take = 100, CancellationToken ct = default);
}
