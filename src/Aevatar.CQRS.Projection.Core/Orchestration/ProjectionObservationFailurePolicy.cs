using Aevatar.Foundation.Abstractions.Persistence;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionObservationFailurePolicy
{
    public static bool ShouldPropagate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            EventStoreOptimisticConcurrencyException => true,
            ProjectionDispatchAggregateException aggregate =>
                aggregate.Failures.Any(static failure => ShouldPropagate(failure.Exception)),
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(ShouldPropagate),
            _ when exception.InnerException is not null => ShouldPropagate(exception.InnerException),
            _ => false,
        };
    }

    public static bool ContainsOcc(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            EventStoreOptimisticConcurrencyException => true,
            ProjectionDispatchAggregateException aggregate =>
                aggregate.Failures.Any(static f => ContainsOcc(f.Exception)),
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(ContainsOcc),
            _ when exception.InnerException is not null => ContainsOcc(exception.InnerException),
            _ => false,
        };
    }
}
