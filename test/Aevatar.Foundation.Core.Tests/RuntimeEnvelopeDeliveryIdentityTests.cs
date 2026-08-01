using Aevatar.Foundation.Runtime.Delivery;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RuntimeEnvelopeDeliveryIdentityTests
{
    [Fact]
    public void ResolveOriginId_ShouldUseStableDeliveryLineagePrecedence()
    {
        var operationEnvelope = new EventEnvelope
        {
            Id = "envelope-id",
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = "operation-id",
                },
                Retry = new EnvelopeRetryContext
                {
                    OriginEventId = "retry-origin-id",
                },
            },
        };
        var retryEnvelope = operationEnvelope.Clone();
        retryEnvelope.Runtime.Deduplication.OperationId = string.Empty;
        var baseEnvelope = retryEnvelope.Clone();
        baseEnvelope.Runtime.Retry.OriginEventId = string.Empty;

        RuntimeEnvelopeDeliveryIdentity.ResolveOriginId(operationEnvelope).Should().Be("operation-id");
        RuntimeEnvelopeDeliveryIdentity.ResolveOriginId(retryEnvelope).Should().Be("retry-origin-id");
        RuntimeEnvelopeDeliveryIdentity.ResolveOriginId(baseEnvelope).Should().Be("envelope-id");
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    public void GetAttempt_ShouldReturnNonNegativeRetryAttempt(int attempt, int expected)
    {
        var envelope = new EventEnvelope
        {
            Runtime = new EnvelopeRuntime
            {
                Retry = new EnvelopeRetryContext { Attempt = attempt },
            },
        };

        RuntimeEnvelopeDeliveryIdentity.GetAttempt(envelope).Should().Be(expected);
    }
}
