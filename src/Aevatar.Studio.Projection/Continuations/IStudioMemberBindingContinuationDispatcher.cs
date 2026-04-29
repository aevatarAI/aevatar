using Aevatar.GAgents.StudioMember;

namespace Aevatar.Studio.Projection.Continuations;

internal interface IStudioMemberBindingContinuationDispatcher
{
    Task DispatchAsync(StudioMemberBindingRequestedEvent request, CancellationToken ct = default);
}
