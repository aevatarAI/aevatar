using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Integration.Tests.Protocols;
using Google.Protobuf;

namespace Aevatar.Integration.Tests;

public sealed class ClaimMessageSinkGAgent : GAgentBase<ClaimSinkState>
{
    [EventHandler]
    public Task HandleAnalystRequested(ClaimAnalystReviewRequested evt) =>
        PersistDomainEventAsync(evt.Clone(), CancellationToken.None);

    [EventHandler]
    public Task HandleFraudRequested(ClaimFraudScoringRequested evt) =>
        PersistDomainEventAsync(evt.Clone(), CancellationToken.None);

    [EventHandler]
    public Task HandleComplianceRequested(ClaimComplianceCheckRequested evt) =>
        PersistDomainEventAsync(evt.Clone(), CancellationToken.None);

    [EventHandler]
    public Task HandleManualReviewRequested(ClaimManualReviewRequested evt) =>
        PersistDomainEventAsync(evt.Clone(), CancellationToken.None);

    protected override ClaimSinkState TransitionState(ClaimSinkState current, IMessage evt)
    {
        var next = current.Clone();
        next.MessageTypes.Add(evt.Descriptor.Name);
        return next;
    }
}
