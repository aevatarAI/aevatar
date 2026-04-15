using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Projection port that activates the materialization scope for the
/// channel bot registration store. Must be called before reading from the
/// projection read model — without activation, the scope agent never
/// subscribes to the actor's event stream and the projector never runs.
/// </summary>
public sealed class ChannelBotRegistrationProjectionPort
    : MaterializationProjectionPortBase<ChannelBotRegistrationMaterializationRuntimeLease>
{
    public ChannelBotRegistrationProjectionPort(
        IProjectionScopeActivationService<ChannelBotRegistrationMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<ChannelBotRegistrationMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = "channel-bot-registration",
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
