using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Framework-internal publisher for Event Sourcing committed state-event notifications.
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
