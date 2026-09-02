using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
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
}
