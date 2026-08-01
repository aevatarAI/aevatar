using Aevatar.Foundation.Runtime.Delivery;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RuntimeEnvelopeDeliveryIdentityTests
{
    [Fact]
    public void ResolveDeliveryLineageId_ShouldUseStableDeliveryLineagePrecedence()
    {
        var operationEnvelope = new EventEnvelope
        {
            Id = "envelope-id",
            Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
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
        retryEnvelope.Runtime.DeliveryIdentity.OperationId = string.Empty;
        var baseEnvelope = retryEnvelope.Clone();
        baseEnvelope.Runtime.Retry.OriginEventId = string.Empty;

        RuntimeEnvelopeDeliveryIdentity.ResolveDeliveryLineageId(operationEnvelope).Should().Be("operation-id");
        RuntimeEnvelopeDeliveryIdentity.ResolveDeliveryLineageId(retryEnvelope).Should().Be("retry-origin-id");
        RuntimeEnvelopeDeliveryIdentity.ResolveDeliveryLineageId(baseEnvelope).Should().Be("envelope-id");
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
