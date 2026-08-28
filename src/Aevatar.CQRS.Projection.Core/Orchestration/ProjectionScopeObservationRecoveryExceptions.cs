using Aevatar.Foundation.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionScopeInFlightObservationPendingException : InvalidOperationException,
    IRuntimeEnvelopeRetryableException
{
    public ProjectionScopeInFlightObservationPendingException(
        ProjectionSourceCoordinate pending,
        ProjectionSourceCoordinate received)
        : base(
            $"Projection scope has pending source '{Format(pending)}' and cannot admit " +
            $"different source '{Format(received)}'.")
    {
    }

    private static string Format(ProjectionSourceCoordinate source) =>
        $"{source.ActorId}@{source.StateVersion}:{source.EventId}";
}

public sealed class ProjectionSourceCoordinateConflictException : InvalidOperationException,
    IRuntimeEnvelopeRetryableException
{
    public ProjectionSourceCoordinateConflictException(
        ProjectionSourceCoordinate committed,
        ProjectionSourceCoordinate received)
        : base(
            $"Projection source version {received.StateVersion} for actor '{received.ActorId}' " +
            $"was committed as event '{committed.EventId}' and cannot accept conflicting " +
            $"event '{received.EventId}'.")
    {
    }
}

public sealed class ProjectionSourceCoordinateInvalidException : InvalidOperationException,
    IRuntimeEnvelopeRetryableException
{
    public ProjectionSourceCoordinateInvalidException(string reason)
        : base($"Projection source coordinate is invalid: {reason}")
    {
    }
}
