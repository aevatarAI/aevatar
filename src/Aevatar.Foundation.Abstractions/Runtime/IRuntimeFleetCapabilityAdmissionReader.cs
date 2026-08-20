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

    // Persisted phased status-route contract + epoch-fenced status document. Phase-A bridge
    // binaries continue to serve routes carrying it but advertise the distinct quiescence
    // contract instead, so they cannot create a fresh V2 OPEN grant.
    public const string ProjectionScopeStatusTerminalV2 =
        "aevatar.projection.scope-status-terminal.v2";

    public const string ProjectionScopeStatusTerminalQuiescenceV1 =
        "aevatar.projection.scope-status-terminal.quiescence.v1";

    public const int ProjectionScopeStatusTerminalReaderVersionV2 = 2;

    public const int ProjectionScopeStatusTerminalReaderVersion =
        ProjectionScopeStatusTerminalReaderVersionV2;

    // Reader revision of the distinct Phase-A bridge contract proving exact writer identity and
    // drained-version release support. Status routes themselves remain V2/2.
    public const int ProjectionScopeStatusTerminalQuiescenceReaderVersion = 3;

    // Fresh Phase-B activation seal. This does not replace the persisted V2 status route
    // contract: it proves that every active runtime can reject sealed actor rows on an older
    // binary before a source is allowed to start or resume a cutover.
    public const string ProjectionScopeStatusTerminalActivationSealV1 =
        "aevatar.projection.scope-status-terminal.activation-seal.v1";

    public const int ProjectionScopeStatusTerminalActivationSealReaderVersion = 4;

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
/// Read-model port for a durable terminal quiescence marker. The returned evidence describes the
/// historical fleet that closed the gate; it is not live membership admission for another rollout.
/// </summary>
public interface IRuntimeFleetCapabilityQuiescenceReader
{
    Task<RuntimeFleetCapabilityQuiescenceEvidence?> GetQuiescenceAsync(
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

public sealed class DenyAllRuntimeFleetCapabilityQuiescenceReader
    : IRuntimeFleetCapabilityQuiescenceReader
{
    public Task<RuntimeFleetCapabilityQuiescenceEvidence?> GetQuiescenceAsync(
        RuntimeFleetCapability capability,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<RuntimeFleetCapabilityQuiescenceEvidence?>(null);
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
    /// <summary>
    /// Whether this host composition can actually provide the advertised reader contract. Optional
    /// modules can stay registered for DI enumeration while suppressing a capability whose runtime
    /// support is absent.
    /// </summary>
    bool IsAvailable => true;

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
