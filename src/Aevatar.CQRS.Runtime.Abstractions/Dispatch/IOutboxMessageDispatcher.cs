using Aevatar.CQRS.Runtime.Abstractions.Persistence;

namespace Aevatar.CQRS.Runtime.Abstractions.Dispatch;

public interface IOutboxMessageDispatcher
{
    Task DispatchAsync(OutboxMessage message, CancellationToken ct = default);
}
