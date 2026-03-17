using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class LoggingProjectionStoreDispatchCompensator<TReadModel>
    : IProjectionStoreDispatchCompensator<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly ILogger<LoggingProjectionStoreDispatchCompensator<TReadModel>> _logger;

    public LoggingProjectionStoreDispatchCompensator(
        ILogger<LoggingProjectionStoreDispatchCompensator<TReadModel>>? logger = null)
    {
        _logger = logger ?? NullLogger<LoggingProjectionStoreDispatchCompensator<TReadModel>>.Instance;
    }

    public Task CompensateAsync(
        ProjectionStoreDispatchCompensationContext<TReadModel> context,
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
