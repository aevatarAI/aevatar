namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandDispatchCleanupAware
{
    Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default);
}
