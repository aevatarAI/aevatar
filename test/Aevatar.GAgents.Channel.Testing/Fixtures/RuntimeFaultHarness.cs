using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Bridges adapter fault tests to the runtime-level durability contracts exercised by RFC §8.3.
/// </summary>
/// <remarks>
/// Adapter authors who want to cover durable-inbox, shard-leader, or projector dispatch semantics supply a concrete
/// harness. Tests that depend on this harness self-skip when no harness is provided so the adapter-only subset of the
/// fault suite still runs.
/// </remarks>
public abstract class RuntimeFaultHarness
{
    /// <summary>
    /// Returns whether the harness owns a durable inbox the fault tests can crash and restart.
    /// </summary>
    public virtual bool SupportsDurableInbox => false;

    /// <summary>
    /// Returns whether the harness exposes a shard leader lease test surface.
    /// </summary>
    public virtual bool SupportsShardLeaderLease => false;

    /// <summary>
    /// Returns whether the harness owns a projector dispatcher that can tombstone entries.
    /// </summary>
    public virtual bool SupportsProjectorDispatcher => false;

    /// <summary>
    /// Returns whether the harness owns a redactor pipeline exercised by fail-closed fault tests.
    /// </summary>
    public virtual bool SupportsRedactorPipeline => false;

    /// <summary>
    /// Returns whether the harness models proactive-command failure bookkeeping (processed_command_ids).
    /// </summary>
    public virtual bool SupportsProactiveCommandFailures => false;

    /// <summary>
    /// Returns whether the harness can drive the inbound working buffer to saturation and observe backpressure.
    /// </summary>
    public virtual bool SupportsInboundSaturation => false;

    /// <summary>
    /// Returns whether the harness can exercise the forensic quarantine path (redactor breaker overflow).
    /// </summary>
    public virtual bool SupportsQuarantineOverflow => false;

    /// <summary>
    /// Runs one durable-commit, then simulates a crash and verifies the activity is replayed on restart.
    /// </summary>
    /// <remarks>
    /// Implementations must commit the ingress event, then crash before consuming it, then restart and confirm the bot
    /// executes the activity exactly once on the next boot.
    /// </remarks>
    public virtual Task SimulateCommitCrashConsumeGapAsync(InboundActivitySeed seed, CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support durable-inbox simulation.");

    /// <summary>
    /// Simulates two silos concurrently consuming the same durable activity and returns how many turns fired.
    /// </summary>
    public virtual Task<int> SimulateConcurrentSiloConsumeAsync(InboundActivitySeed seed, CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support multi-silo consume simulation.");

    /// <summary>
    /// Projects one snapshot carrying a tombstoned entry and returns whether the dispatcher's DeleteAsync fired.
    /// </summary>
    public virtual Task<bool> ProjectTombstonedEntryAsync(string entryId, CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support projector dispatcher simulation.");

    /// <summary>
    /// Runs the housekeeping pass while the projector is known to lag and returns whether the tombstone survives.
    /// </summary>
    public virtual Task<bool> HousekeepingPreservesTombstoneUnderLagAsync(string entryId, CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support projector lag simulation.");

    /// <summary>
    /// Races two supervisors for one shard and returns the winning lease epoch and the loser's result.
    /// </summary>
    public virtual Task<ShardLeaderOutcome> RaceShardLeadersAsync(string shardKey, CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support shard leader simulation.");

    /// <summary>
    /// Submits a stale write using an earlier lease epoch and returns whether it was rejected.
    /// </summary>
    public virtual Task<bool> RejectsStaleLeaseEpochWriteAsync(string shardKey, long staleEpoch, CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support shard leader simulation.");

    /// <summary>
    /// Attempts to commit a non-monotonic last_seq and returns whether the harness rejected the write.
    /// </summary>
    public virtual Task<bool> RejectsNonMonotonicLastSeqAsync(string shardKey, CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support shard leader simulation.");

    /// <summary>
    /// Forces the redactor to throw on the next call and returns whether the ingress commit was skipped.
    /// </summary>
    public virtual Task<bool> RedactorThrowsFailsClosedAsync(CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support redactor pipeline simulation.");

    /// <summary>
    /// Drives a proactive send whose credential resolution fails and returns the recorded outcome.
    /// </summary>
    public virtual Task<ProactiveCommandOutcome> DriveCredentialResolutionFailureAsync(
        string commandId,
        CancellationToken ct) => throw new NotSupportedException("Harness does not support proactive command simulation.");

    /// <summary>
    /// Drives a proactive send where the adapter returns a transient failure and returns the recorded outcome.
    /// </summary>
    public virtual Task<ProactiveCommandOutcome> DriveTransientAdapterErrorAsync(
        string commandId,
        CancellationToken ct) => throw new NotSupportedException("Harness does not support proactive command simulation.");

    /// <summary>
    /// Drives a proactive send where the adapter returns a permanent failure and returns the recorded outcome.
    /// </summary>
    public virtual Task<ProactiveCommandOutcome> DrivePermanentAdapterErrorAsync(
        string commandId,
        CancellationToken ct) => throw new NotSupportedException("Harness does not support proactive command simulation.");

    /// <summary>
    /// Drives the bounded inbound working buffer to capacity and returns what the adapter did: did a backpressure
    /// signal get raised, and were any committed events silently dropped during saturation.
    /// </summary>
    public virtual Task<InboundSaturationOutcome> DriveInboundSaturationAsync(CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support inbound saturation simulation.");

    /// <summary>
    /// Overflows the forensic quarantine queue and returns both whether the oldest entry was evicted and whether an
    /// alert was emitted in response.
    /// </summary>
    public virtual Task<QuarantineOverflowOutcome> DriveQuarantineOverflowAsync(CancellationToken ct) =>
        throw new NotSupportedException("Harness does not support quarantine overflow simulation.");
}

/// <summary>
/// Outcome of a shard leader race.
/// </summary>
/// <param name="WinnerLeaseEpoch">The lease epoch held by the winning supervisor.</param>
/// <param name="LoserRejected">Whether the loser's write was rejected by the session store.</param>
public sealed record ShardLeaderOutcome(long WinnerLeaseEpoch, bool LoserRejected);

/// <summary>
/// Outcome of a proactive command driven by a fault harness.
/// </summary>
/// <param name="FailureCode">The error code the runtime recorded on the failure event, if any.</param>
/// <param name="Retryable">Whether the command id is retryable after the recorded failure.</param>
/// <param name="CredentialUsed">The credential kind actually used for the outbound attempt.</param>
public sealed record ProactiveCommandOutcome(string? FailureCode, bool Retryable, PrincipalKind CredentialUsed);

/// <summary>
/// Outcome of driving the inbound working buffer to saturation.
/// </summary>
/// <param name="BackpressureSignalRaised">Whether the adapter surfaced a backpressure / overload signal.</param>
/// <param name="CommittedEventSilentlyDropped">
/// Whether any committed event was dropped without either entering the working buffer or surfacing an explicit error.
/// </param>
public sealed record InboundSaturationOutcome(bool BackpressureSignalRaised, bool CommittedEventSilentlyDropped);

/// <summary>
/// Outcome of overflowing the forensic quarantine path.
/// </summary>
/// <param name="OldestEntryEvicted">Whether the oldest queued entry was evicted in favor of the newer payload.</param>
/// <param name="AlertEmitted">Whether the overflow event fired an operator-visible alert.</param>
public sealed record QuarantineOverflowOutcome(bool OldestEntryEvicted, bool AlertEmitted);
