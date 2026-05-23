using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentRunTerminalProjectionPort
    : ServiceProjectionPortBase<GAgentRunTerminalProjectionContext>,
      IGAgentRunTerminalProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> _attachExistingLeaseLookup;

    public GAgentRunTerminalProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> releaseService,
        IProjectionScopeAttachExistingLeaseLookup<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> attachExistingLeaseLookup)
        : base(options, activationService, releaseService, ServiceProjectionKinds.GAgentRunTerminalDraftRun)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
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

    // Refactor (iter37/cluster-037-gagentservice-binders-attach-existing):
    //   Old pattern: GAgentService interaction binders synchronously prime projection sessions before dispatch(request-path projection activation in BindAsync).
    //   New principle: Attach-only to existing projection sessions/materialization leases via capability-specific attach-existing ports.
    //   Cold sessions return ProjectionUnavailable / pending before dispatch; no top-level live-observation exception.
    public async Task<IGAgentRunTerminalProjectionLease?> AttachExistingProjectionAsync(
        string actorId,
        string correlationId,
        GAgentRunTerminalInteractionKind interactionKind,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            return null;
        }

        var projectionKind = ResolveProjectionKind(interactionKind);
        var scopeKey = new ProjectionRuntimeScopeKey(
            actorId,
            projectionKind,
            ProjectionRuntimeMode.DurableMaterialization,
            correlationId);
        // Refactor (iter49/cluster-049-gagentservice-runtime-attach-existing-side-read):
        //   Old pattern: Capability projection ports duplicated runtime existence checks via IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()).
        //   New principle: Projection Core exposes typed attach-existing lease/session lookup contract; capability ports delegate to contract instead of runtime actor-id side reads.
        var runtimeLease = await _attachExistingLeaseLookup.TryGetAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                Mode = scopeKey.Mode,
                SessionId = scopeKey.SessionId,
            },
            ct).ConfigureAwait(false);
        if (runtimeLease == null)
            return null;

        return new GAgentRunTerminalProjectionLease(runtimeLease);
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
