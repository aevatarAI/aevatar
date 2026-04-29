using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Projection.CommandServices;

namespace Aevatar.Studio.Projection.Continuations;

internal sealed class StudioMemberBindingContinuationGAgent : GAgentBase
{
    private readonly StudioMemberBindingContinuationService _continuationService;

    public StudioMemberBindingContinuationGAgent(
        StudioMemberBindingContinuationService continuationService)
    {
        _continuationService = continuationService
            ?? throw new ArgumentNullException(nameof(continuationService));
    }

    [EventHandler(EndpointName = "continueStudioMemberBinding")]
    public Task HandleContinuationRequestedAsync(StudioMemberBindingContinuationRequestedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Request is null)
            throw new InvalidOperationException("Studio member binding continuation request is missing.");

        return _continuationService.HandleRequestedAsync(command.Request);
    }
}
