using Aevatar.CQRS.Sagas.Abstractions.Actions;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Sagas.Core.Runtime;

public sealed class NoopSagaTimeoutScheduler : ISagaTimeoutScheduler
{
    private readonly ILogger<NoopSagaTimeoutScheduler> _logger;

    public NoopSagaTimeoutScheduler(ILogger<NoopSagaTimeoutScheduler> logger)
    {
        _logger = logger;
    }

    public Task ScheduleAsync(
        string sagaName,
        string correlationId,
        string actorId,
        SagaScheduleTimeoutAction action,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Saga timeout scheduled (noop). saga={SagaName}, correlationId={CorrelationId}, actorId={ActorId}, timeout={TimeoutName}, delay={DelayMs}ms",
            sagaName,
            correlationId,
            actorId,
            action.TimeoutName,
            action.Delay.TotalMilliseconds);

        return Task.CompletedTask;
    }
}
