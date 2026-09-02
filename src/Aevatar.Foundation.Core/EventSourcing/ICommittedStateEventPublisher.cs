using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Framework-internal publisher for Event Sourcing committed state-event notifications.
/// Successful completion means the configured runtime stream accepted the envelope.
/// It does not mean that any observer consumed it or that a read model is visible.
/// </summary>
internal interface ICommittedStateEventPublisher
{
    Task PublishAsync(
        CommittedStateEventPublished evt,
        ObserverAudience audience = ObserverAudience.CommittedFacts,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null);
}
