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

    public const string ProjectionScopeStatusTerminalV1 =
        "aevatar.projection.scope-status-terminal.v1";

    // The status document contract (ProjectionScopeStatusDocument incl. its status_route write
    // fence) is unchanged since the terminal materializer shipped; the phased cutover and the
    // epoch fence are additive route/evaluator semantics that older readers tolerate (they
    // treat any terminal route as writable and produce byte-identical documents for the same
    // route epoch). Keeping the reader version at 1 keeps every source rollback-safe: a v1
    // reader can still serve every route this reader creates.
    public const int ProjectionScopeStatusTerminalReaderVersion = 1;

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
