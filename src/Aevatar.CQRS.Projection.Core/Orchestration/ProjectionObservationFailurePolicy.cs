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
            ProjectionScopeInFlightObservationPendingException => true,
            ProjectionSourceCoordinateConflictException => true,
            ProjectionSourceCoordinateInvalidException => true,
            // A blocked status route refuses the observation until the route is flipped; the
            // envelope must be redelivered, never swallowed.
            ProjectionScopeStatusRouteBlockedException => true,
            // A route already names a Phase-B writer, but its current proof set is not visible.
            // Swallowing the publication would advance the transport checkpoint permanently.
            ProjectionScopeStatusPhaseBProofUnavailableException => true,
            // The terminal status writer could not apply the document (Conflict/Gap). It reaches
            // the source scope wrapped in the relay's aggregate: the observation must fail so it
            // is redelivered and no checkpoint advances on an unproved status write.
            ProjectionScopeStatusWriteRejectedException => true,
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
