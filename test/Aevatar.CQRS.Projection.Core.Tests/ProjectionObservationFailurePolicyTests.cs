using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionObservationFailurePolicyTests
{
    [Fact]
    public void ShouldPropagate_ShouldReturnTrue_ForOptimisticConcurrencyException()
    {
        var exception = new EventStoreOptimisticConcurrencyException("actor-1", 4, 5);

        ProjectionObservationFailurePolicy.ShouldPropagate(exception).Should().BeTrue();
    }

    [Fact]
    public void ShouldPropagate_ShouldReturnTrue_ForProjectionDispatchAggregateContainingOptimisticConcurrencyException()
    {
        var aggregate = new ProjectionDispatchAggregateException(
        [
            new ProjectionDispatchFailure(
                "projector",
                1,
                new EventStoreOptimisticConcurrencyException("actor-2", 7, 8)),
        ]);

        ProjectionObservationFailurePolicy.ShouldPropagate(aggregate).Should().BeTrue();
    }

    [Fact]
    public void ShouldPropagate_ShouldReturnFalse_ForDeterministicProjectionFailure()
    {
        var aggregate = new ProjectionDispatchAggregateException(
        [
            new ProjectionDispatchFailure(
                "projector",
                1,
                new InvalidOperationException("projection failed")),
        ]);

        ProjectionObservationFailurePolicy.ShouldPropagate(aggregate).Should().BeFalse();
    }
}
