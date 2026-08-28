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

    [Theory]
    [InlineData(1, 5000)]
    [InlineData(2, 10000)]
    [InlineData(3, 20000)]
    [InlineData(4, 30000)]
    [InlineData(40, 30000)]
    public void RetryUntilResolvedDelay_ShouldUseBoundedExponentialBackoff(
        int nextAttempt,
        int expectedDelayMs)
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("0", "0");

        policy.ResolveRetryDelayMs(nextAttempt, retryUntilResolved: true)
            .Should().Be(expectedDelayMs);
        policy.ResolveRetryDelayMs(nextAttempt, retryUntilResolved: false)
            .Should().Be(0);
    }

    [Theory]
    [InlineData(1, 5000, 6000)]
    [InlineData(2, 10000, 12000)]
    [InlineData(3, 20000, 24000)]
    [InlineData(4, 24000, 30000)]
    [InlineData(40, 24000, 30000)]
    public void RetryUntilResolvedDelay_WithStableIdentity_ShouldUseBoundedJitterBand(
        int nextAttempt,
        int minimumDelayMs,
        int maximumDelayMs)
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("0", "0");
        const string jitterIdentity = "status-materializer|source-scope";

        var first = policy.ResolveRetryDelayMs(
            nextAttempt,
            retryUntilResolved: true,
            jitterIdentity);
        var repeated = policy.ResolveRetryDelayMs(
            nextAttempt,
            retryUntilResolved: true,
            jitterIdentity);

        first.Should().BeInRange(minimumDelayMs, maximumDelayMs);
        repeated.Should().Be(first, "the same durable retry must keep its delay across redelivery and restart");
    }

    [Fact]
    public void RetryUntilResolvedDelay_ShouldSpreadIndependentCallbackIdentities()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("0", "0");

        var delays = Enumerable.Range(0, 64)
            .Select(index => policy.ResolveRetryDelayMs(
                nextAttempt: 1,
                retryUntilResolved: true,
                $"status-materializer|source-{index}"))
            .Distinct()
            .ToArray();

        delays.Should().HaveCountGreaterThan(16);
        delays.Should().OnlyContain(delay => delay >= 5000 && delay <= 6000);
    }

    [Fact]
    public void RetryUntilResolvedMarker_ShouldRemainSafelyDelayedWhenOrdinaryRetryIsDisabled()
    {
        var policy = RuntimeEnvelopeRetryPolicy.FromValues("0", "0");

        policy.TryBuildRetryEnvelope(
                new EventEnvelope { Id = "retry-until-resolved-disabled-policy" },
                new RuntimeRetryUntilResolvedTestException(),
                out _,
                out var nextAttempt)
            .Should().BeTrue();

        nextAttempt.Should().Be(1);
        policy.ResolveRetryDelayMs(nextAttempt, retryUntilResolved: true).Should().Be(5000);
        policy.ResolveRetryDelayMs(
                nextAttempt,
                retryUntilResolved: true,
                stableJitterIdentity: "disabled-policy-callback")
            .Should().BeInRange(5000, 6000);
    }

    [Fact]
    public void ResolveRetryCoalescingCursor_ShouldReadWrappedAuthoritativeCursor()
    {
        var cursor = new RuntimeEnvelopeRetryCoalescingCursor("source-scope", 17);
        var exception = new AggregateException(
            new InvalidOperationException(
                "wrapper",
                new RuntimeRetryCoalescingTestException(cursor)));

        RuntimeEnvelopeRetryPolicy.ResolveRetryCoalescingCursor(exception)
            .Should().Be(cursor);
        RuntimeEnvelopeRetryPolicy.ResolveRetryCoalescingCursor(
                new RuntimeRetryUntilResolvedTestException())
            .Should().BeNull();
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

    private sealed class RuntimeRetryCoalescingTestException(
        RuntimeEnvelopeRetryCoalescingCursor cursor)
        : Exception, IRuntimeEnvelopeRetryCoalescingException
    {
        public RuntimeEnvelopeRetryCoalescingCursor RetryCoalescingCursor { get; } = cursor;
    }
}
