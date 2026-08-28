using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeEnvelopeRetryPolicyTests
{
    [Fact]
    public void PublicationFailure_ShouldBeRetryableByDefault()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues(null, null);
        var source = new EventEnvelope { Id = "publication-source" };
        var stateEvent = new StateEvent
        {
            AgentId = "publication-actor",
            EventId = "committed-event",
            Version = 1,
        };

        var retryable = policy.TryBuildRetryEnvelope(
            source,
            new CommittedStatePublicationException(
                "publication-actor",
                stateEvent,
                CommittedStatePublicationFailureStage.AdapterAcceptance,
                new InvalidOperationException("injected")),
            out var retry,
            out var nextAttempt);

        retryable.Should().BeTrue();
        nextAttempt.Should().Be(1);
        retry.Runtime.Retry.LastErrorType.Should().Be(nameof(CommittedStatePublicationException));
    }

    [Fact]
    public void RuntimeRetryableMarker_ShouldBeRetryableByDefaultThroughWrappers()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues(null, null);
        var source = new EventEnvelope { Id = "runtime-retryable-source" };
        var marker = new RuntimeRetryableTestException();

        policy.TryBuildRetryEnvelope(
                source,
                new AggregateException(new InvalidOperationException("wrapper", marker)),
                out var retry,
                out var nextAttempt)
            .Should().BeTrue();

        nextAttempt.Should().Be(1);
        retry.Runtime.Retry.LastErrorType.Should().Be(nameof(AggregateException));
    }

    [Fact]
    public void RetryUntilResolvedMarker_ShouldContinuePastOrdinaryAttemptBudgetThroughWrappers()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("3", "10");
        var source = new EventEnvelope
        {
            Id = "retry-until-resolved-source",
            Runtime = new EnvelopeRuntime
            {
                Retry = new EnvelopeRetryContext
                {
                    Attempt = 3,
                    OriginEventId = "retry-until-resolved-lineage",
                },
            },
        };

        policy.TryBuildRetryEnvelope(
                source,
                new AggregateException(new RuntimeRetryUntilResolvedTestException()),
                out var retry,
                out var nextAttempt)
            .Should().BeTrue();

        nextAttempt.Should().Be(4);
        retry.Runtime.Retry.Attempt.Should().Be(4);
        retry.Runtime.Retry.OriginEventId.Should().Be("retry-until-resolved-lineage");
        RuntimeEnvelopeRetryPolicy
            .ContainsRuntimeEnvelopeRetryUntilResolvedFailure(
                new InvalidOperationException(
                    "wrapper",
                    new RuntimeRetryUntilResolvedTestException()))
            .Should().BeTrue();
    }

    [Fact]
    public void OrdinaryRuntimeRetryableMarker_ShouldStopAtAttemptBudget()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("3", "10");
        var source = new EventEnvelope
        {
            Id = "ordinary-runtime-retryable-source",
            Runtime = new EnvelopeRuntime
            {
                Retry = new EnvelopeRetryContext { Attempt = 3 },
            },
        };

        policy.TryBuildRetryEnvelope(
                source,
                new RuntimeRetryableTestException(),
                out _,
                out var nextAttempt)
            .Should().BeFalse();

        nextAttempt.Should().Be(4);
    }

    [Theory]
    [InlineData(typeof(EventStoreOptimisticConcurrencyException))]
    [InlineData(typeof(EventStoreVersionDriftException))]
    [InlineData(typeof(CommittedStatePublicationException))]
    public void ContainsCommitConsistencyFailure_MatchesCommitBoundaryExceptions(Type exceptionType)
    {
        var exception = BuildCommitBoundaryException(exceptionType);

        RuntimeEnvelopeRetryPolicy.ContainsCommitConsistencyFailure(exception).Should().BeTrue();
        RuntimeEnvelopeRetryPolicy
            .ContainsCommitConsistencyFailure(new InvalidOperationException("wrap", exception))
            .Should().BeTrue();
        RuntimeEnvelopeRetryPolicy
            .ContainsCommitConsistencyFailure(new AggregateException(exception))
            .Should().BeTrue();
    }

    [Fact]
    public void ContainsCommitConsistencyFailure_IgnoresUnrelatedFailures()
    {
        RuntimeEnvelopeRetryPolicy
            .ContainsCommitConsistencyFailure(new InvalidOperationException("unrelated"))
            .Should().BeFalse();
        RuntimeEnvelopeRetryPolicy
            .ContainsCommitConsistencyFailure(new TimeoutException("slow"))
            .Should().BeFalse();
    }

    [Fact]
    public void ContainsRuntimeEnvelopeRetryableFailure_ShouldMatchWrappedMarkerOnly()
    {
        var marker = new RuntimeRetryableTestException();

        RuntimeEnvelopeRetryPolicy.ContainsRuntimeEnvelopeRetryableFailure(marker).Should().BeTrue();
        RuntimeEnvelopeRetryPolicy
            .ContainsRuntimeEnvelopeRetryableFailure(new InvalidOperationException("wrap", marker))
            .Should().BeTrue();
        RuntimeEnvelopeRetryPolicy
            .ContainsRuntimeEnvelopeRetryableFailure(new AggregateException(marker))
            .Should().BeTrue();
        RuntimeEnvelopeRetryPolicy
            .ContainsRuntimeEnvelopeRetryableFailure(
                new EventStoreOptimisticConcurrencyException("actor", expectedVersion: 1, actualVersion: 2))
            .Should().BeFalse();
        RuntimeEnvelopeRetryPolicy
            .ContainsRuntimeEnvelopeRetryableFailure(new InvalidOperationException("ordinary"))
            .Should().BeFalse();
    }

    private static Exception BuildCommitBoundaryException(Type exceptionType)
    {
        if (exceptionType == typeof(EventStoreOptimisticConcurrencyException))
            return new EventStoreOptimisticConcurrencyException("actor", expectedVersion: 4, actualVersion: 2);
        if (exceptionType == typeof(EventStoreVersionDriftException))
            return new EventStoreVersionDriftException("actor", replayedVersion: 6, storeVersion: 4);

        var stateEvent = new StateEvent
        {
            AgentId = "actor",
            EventId = "committed-event",
            Version = 1,
        };
        return new CommittedStatePublicationException(
            "actor",
            stateEvent,
            CommittedStatePublicationFailureStage.AdapterAcceptance,
            new InvalidOperationException("injected"));
    }

    private sealed class RuntimeRetryableTestException : Exception, IRuntimeEnvelopeRetryableException
    {
    }

    private sealed class RuntimeRetryUntilResolvedTestException
        : Exception, IRuntimeEnvelopeRetryUntilResolvedException
    {
    }
}
