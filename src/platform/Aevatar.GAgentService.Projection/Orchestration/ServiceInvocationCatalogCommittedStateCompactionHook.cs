using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Services;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Orchestration;

internal sealed class ServiceInvocationCatalogCommittedStateCompactionHook : ICommittedStatePublicationHook
{
    public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.ActorType != typeof(ServiceInvocationCatalogGAgent) ||
            context.Published.StateRoot?.Is(ServiceInvocationCatalogState.Descriptor) != true)
        {
            return Task.CompletedTask;
        }

        var state = context.Published.StateRoot.Unpack<ServiceInvocationCatalogState>();
        ServiceInvocationCatalogCompaction.Compact(state);
        context.Published.StateRoot = Any.Pack(state);

        if (context.Published.StateEvent?.EventData?.Is(ServiceInvocationCatalogObservedEvent.Descriptor) == true)
        {
            var stateEvent = context.Published.StateEvent.EventData.Unpack<ServiceInvocationCatalogObservedEvent>();
            ServiceInvocationCatalogCompaction.Compact(stateEvent);
            context.Published.StateEvent.EventData = Any.Pack(stateEvent);
        }

        return Task.CompletedTask;
    }
}
