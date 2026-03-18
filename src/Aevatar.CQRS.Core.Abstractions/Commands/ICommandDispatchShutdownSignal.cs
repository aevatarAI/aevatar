namespace Aevatar.CQRS.Core.Abstractions.Commands;

/// <summary>
/// Provides a shutdown cancellation token for detached command dispatch services.
/// When no implementation is registered, services default to <see cref="CancellationToken.None"/>.
/// </summary>
public interface ICommandDispatchShutdownSignal
{
    CancellationToken ShutdownToken { get; }
}
