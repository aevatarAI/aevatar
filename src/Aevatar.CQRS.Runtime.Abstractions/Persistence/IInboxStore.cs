namespace Aevatar.CQRS.Runtime.Abstractions.Persistence;

public interface IInboxStore
{
    Task<bool> TryAcquireAsync(string commandId, CancellationToken ct = default);

    Task MarkCompletedAsync(string commandId, CancellationToken ct = default);

    Task MarkFailedAsync(string commandId, string error, CancellationToken ct = default);
}
