using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentRunTerminalProjectionPort
    : ServiceProjectionPortBase<GAgentRunTerminalProjectionContext>,
      IGAgentRunTerminalProjectionPort
{
    public GAgentRunTerminalProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.GAgentRunTerminalDraftRun)
    {
    }

    public async Task<IGAgentRunTerminalProjectionLease?> EnsureProjectionAsync(
        string actorId,
        string correlationId,
        GAgentRunTerminalInteractionKind interactionKind,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(correlationId))
            return null;

        var runtimeLease = await EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ResolveProjectionKind(interactionKind),
                Mode = ProjectionRuntimeMode.DurableMaterialization,
                SessionId = correlationId.Trim(),
            },
            ct);

        return runtimeLease == null
            ? null
            : new GAgentRunTerminalProjectionLease(runtimeLease);
    }

    public Task ReleaseProjectionAsync(
        IGAgentRunTerminalProjectionLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (lease is not GAgentRunTerminalProjectionLease terminalLease)
            throw new InvalidOperationException("Unknown GAgent run terminal projection lease implementation.");

        return ReleaseProjectionAsync(terminalLease.RuntimeLease, ct);
    }

    internal static string ResolveProjectionKind(GAgentRunTerminalInteractionKind interactionKind) =>
        interactionKind switch
        {
            GAgentRunTerminalInteractionKind.DraftRun => ServiceProjectionKinds.GAgentRunTerminalDraftRun,
            GAgentRunTerminalInteractionKind.Approval => ServiceProjectionKinds.GAgentRunTerminalApproval,
            _ => throw new ArgumentOutOfRangeException(nameof(interactionKind), interactionKind, "Unknown GAgent run terminal interaction kind."),
        };

    public static GAgentRunTerminalInteractionKind ResolveInteractionKind(string projectionKind) =>
        projectionKind switch
        {
            ServiceProjectionKinds.GAgentRunTerminalDraftRun => GAgentRunTerminalInteractionKind.DraftRun,
            ServiceProjectionKinds.GAgentRunTerminalApproval => GAgentRunTerminalInteractionKind.Approval,
            _ => throw new ArgumentOutOfRangeException(nameof(projectionKind), projectionKind, "Unknown GAgent run terminal projection kind."),
        };

    private sealed class GAgentRunTerminalProjectionLease : IGAgentRunTerminalProjectionLease
    {
        public GAgentRunTerminalProjectionLease(
            ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext> runtimeLease)
        {
            RuntimeLease = runtimeLease ?? throw new ArgumentNullException(nameof(runtimeLease));
        }

        internal ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext> RuntimeLease { get; }

        public string ActorId => RuntimeLease.Context.RootActorId;

        public string CorrelationId => RuntimeLease.Context.CorrelationId;

        public GAgentRunTerminalInteractionKind InteractionKind => RuntimeLease.Context.InteractionKind;
    }
}
