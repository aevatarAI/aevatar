namespace Aevatar.Foundation.Abstractions.Runtime;

public static class RuntimeFleetCapabilityAuthorityIdentity
{
    public const string ActorId = "runtime-fleet-capability-authority";
    public const string AgentKind = "runtime.fleet-capability-authority";
    public const string ReconcileCallbackId = "runtime-fleet-reconcile";

    public static bool IsReservedCallback(string actorId, string callbackId) =>
        string.Equals(actorId, ActorId, StringComparison.Ordinal) &&
        string.Equals(callbackId, ReconcileCallbackId, StringComparison.Ordinal);
}

public static class RuntimeFleetCapabilityContracts
{
    public const string WorkflowNormalizedStateV1 =
        "aevatar.workflow.normalized-state.v1";

    public const int WorkflowNormalizedStateReaderVersionV1 = 1;

    public const int WorkflowNormalizedStateReaderVersionV2 = 2;

    public const int WorkflowNormalizedStateReaderVersion =
        WorkflowNormalizedStateReaderVersionV2;

    // Previous terminal status contract (phase-unaware source binaries): routes carrying it are
    // still served by the current terminal materializer, but no new route is created under it
    // and the current binary does not advertise it, so a fleet that mixes phase-unaware and
    // phased binaries never opens either gate.
    public const string ProjectionScopeStatusTerminalV1 =
        "aevatar.projection.scope-status-terminal.v1";

    // Phased status-route cutover + epoch-fenced status document. Only binaries running the
    // phased source scope advertise this contract; a source adopts (or upgrades) its status route
    // to this contract only under a fresh V2 admission.
    public const string ProjectionScopeStatusTerminalV2 =
        "aevatar.projection.scope-status-terminal.v2";

    public const int ProjectionScopeStatusTerminalReaderVersion = 2;

    public const string ProjectionIncrementalGraphV1 =
        "aevatar.projection.incremental-graph.v1";

    public const int ProjectionIncrementalGraphReaderVersion = 1;
}

/// <summary>
/// Activation-only read-model port for the durable fleet capability authority.
/// Consumers must fail closed when the returned proof is missing or stale.
/// </summary>
public interface IRuntimeFleetCapabilityAdmissionReader
{
    Task<RuntimeFleetCapabilityAdmission?> GetAsync(
        RuntimeFleetCapability capability,
        CancellationToken ct = default);
}

/// <summary>
/// Narrow runtime-owned view of the local authoritative membership identity.
/// It is deliberately separate from the fleet proof read model so a stale
/// authority document cannot validate itself.
/// </summary>
public interface IRuntimeLocalMembershipIdentityReader
{
    ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(
        CancellationToken ct = default);
}

public sealed record RuntimeLocalMembershipIdentity(
    long MembershipEpoch,
    string MembershipDigest,
    string DeploymentRevision,
    string LocalMemberId,
    string LocalMemberIncarnation);

/// <summary>
/// Default used when a host has not wired the CQRS authority read model.
/// Absence means no new schema cutover; it never fabricates local evidence.
/// </summary>
public sealed class DenyAllRuntimeFleetCapabilityAdmissionReader
    : IRuntimeFleetCapabilityAdmissionReader
{
    public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
        RuntimeFleetCapability capability,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<RuntimeFleetCapabilityAdmission?>(null);
    }
}

/// <summary>
/// Default used outside a trusted cluster membership adapter.
/// </summary>
public sealed class UnavailableRuntimeLocalMembershipIdentityReader
    : IRuntimeLocalMembershipIdentityReader
{
    public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(null);
    }
}

/// <summary>
/// Trusted runtime adapter used only by the fleet authority actor to reconcile
/// current active membership and code capability advertisements.
/// </summary>
public interface IRuntimeFleetMembershipSnapshotSource
{
    Task<RuntimeFleetMembershipSnapshot?> GetCurrentAsync(
        CancellationToken ct = default);
}

/// <summary>
/// One reader contract contributed by a module to the runtime silo manifest.
/// The trusted Orleans membership adapter reads these advertisements from
/// every active silo; business commands cannot supply them.
/// </summary>
public interface IRuntimeFleetCapabilityAdvertisement
{
    RuntimeFleetMemberCapability GetCapability();

    /// <summary>
    /// CLR implementation marker whose module identity participates in the
    /// deployment revision. The default is the advertisement implementation
    /// itself; adapters declared outside the reader assembly must override it
    /// with a type from the assembly that actually reads the advertised state.
    /// </summary>
    Type GetReaderImplementationType() => GetType();
}

public sealed class UnavailableRuntimeFleetMembershipSnapshotSource
    : IRuntimeFleetMembershipSnapshotSource
{
    public Task<RuntimeFleetMembershipSnapshot?> GetCurrentAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<RuntimeFleetMembershipSnapshot?>(null);
    }
}
