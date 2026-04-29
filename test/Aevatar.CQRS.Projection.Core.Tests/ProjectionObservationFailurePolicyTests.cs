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

    [Fact]
    public void ShouldPropagate_ShouldThrow_ForNullException()
    {
        Action act = () => ProjectionObservationFailurePolicy.ShouldPropagate(null!);

        act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("exception");
    }

    [Fact]
    public void ShouldPropagate_ShouldReturnTrue_ForAggregateExceptionContainingOptimisticConcurrencyException()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            new EventStoreOptimisticConcurrencyException("actor-3", 1, 2));

        ProjectionObservationFailurePolicy.ShouldPropagate(aggregate).Should().BeTrue();
    }

    [Fact]
    public void ShouldPropagate_ShouldReturnFalse_ForAggregateExceptionWithOnlyDeterministicFailures()
    {
        var aggregate = new AggregateException(new InvalidOperationException("unrelated"));

        ProjectionObservationFailurePolicy.ShouldPropagate(aggregate).Should().BeFalse();
    }

    [Fact]
    public void ShouldPropagate_ShouldUnwrap_InnerExceptionChain()
    {
        var wrapped = new InvalidOperationException(
            "outer",
            new InvalidOperationException(
                "middle",
                new EventStoreOptimisticConcurrencyException("actor-4", 3, 4)));

        ProjectionObservationFailurePolicy.ShouldPropagate(wrapped).Should().BeTrue();
    }

    [Fact]
    public void ShouldPropagate_ShouldReturnFalse_ForDeterministicExceptionWithoutInner()
    {
        ProjectionObservationFailurePolicy.ShouldPropagate(new InvalidOperationException("boom"))
            .Should().BeFalse();
    }

    [Fact]
    public void ContainsOcc_ShouldReturnTrue_ForDirectOcc()
    {
        var exception = new EventStoreOptimisticConcurrencyException("actor-1", 4, 5);
        ProjectionObservationFailurePolicy.ContainsOcc(exception).Should().BeTrue();
    }

    [Fact]
    public void ContainsOcc_ShouldReturnTrue_ForWrappedOcc()
    {
        var wrapped = new ProjectionDispatchAggregateException(
        [
            new ProjectionDispatchFailure(
                "projector", 1,
                new EventStoreOptimisticConcurrencyException("actor-2", 7, 8)),
        ]);
        ProjectionObservationFailurePolicy.ContainsOcc(wrapped).Should().BeTrue();
    }

    [Fact]
    public void ContainsOcc_ShouldReturnFalse_ForNonOccException()
    {
        ProjectionObservationFailurePolicy.ContainsOcc(new InvalidOperationException("boom"))
            .Should().BeFalse();
    }
}
