using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class LoggingProjectionStoreDispatchCompensator<TReadModel, TKey>
    : IProjectionStoreDispatchCompensator<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    private readonly ILogger<LoggingProjectionStoreDispatchCompensator<TReadModel, TKey>> _logger;

    public LoggingProjectionStoreDispatchCompensator(
        ILogger<LoggingProjectionStoreDispatchCompensator<TReadModel, TKey>>? logger = null)
    {
        _logger = logger ?? NullLogger<LoggingProjectionStoreDispatchCompensator<TReadModel, TKey>>.Instance;
    }

    public Task CompensateAsync(
        ProjectionStoreDispatchCompensationContext<TReadModel, TKey> context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        _logger.LogWarning(
            context.Exception,
            "Projection dispatch compensation executed. readModelType={ReadModelType} operation={Operation} failedStore={FailedStore} succeededStores={SucceededStores}",
            typeof(TReadModel).FullName,
            context.Operation,
            context.FailedStore,
            string.Join(",", context.SucceededStores));
        return Task.CompletedTask;
    }
}
