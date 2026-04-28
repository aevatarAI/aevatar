using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Plugin contract for tombstone-compaction targets owned by per-package agent
/// modules. Each target tells the compactor which actor to address, which
/// projection scope to read the safe watermark from, and which protobuf command
/// to dispatch — but does not perform delivery itself. <see
/// cref="ChannelRuntimeTombstoneCompactor"/> is the only writer; it goes
/// through <see cref="IActorDispatchPort"/> so envelope routing stays on the
/// standard inbox/dispatch path.
/// </summary>
public interface ITombstoneCompactionTarget
{
    /// <summary>Well-known actor id for the compaction-owning aggregate.</summary>
    string ActorId { get; }

    /// <summary>Projection scope used to look up the safe state-version watermark.</summary>
    string ProjectionKind { get; }

    /// <summary>Human-readable label for diagnostics / log lines.</summary>
    string TargetName { get; }

    /// <summary>
    /// Materializes the actor lifecycle (so the dispatched envelope has a live
    /// inbox). Implementations call <see cref="IActorRuntime.GetAsync"/> +
    /// <see cref="IActorRuntime.CreateAsync{TActor}"/> with their concrete
    /// GAgent type — the compactor stays generic.
    /// </summary>
    Task EnsureActorAsync(IActorRuntime actorRuntime, CancellationToken ct);

    /// <summary>Builds the per-target compaction command keyed off the safe state version.</summary>
    IMessage CreateCommand(long safeStateVersion);
}
