using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Shared current-state projector loop for actor states that materialize one read-model document per retained entry.
/// </summary>
/// <typeparam name="TState">The committed actor state message unpacked from the envelope.</typeparam>
/// <typeparam name="TEntry">The entry type enumerated from the authoritative state.</typeparam>
/// <typeparam name="TDocument">The read-model document written by the projection dispatcher.</typeparam>
/// <typeparam name="TContext">The concrete projection materialization context type surfaced by the DI registration.</typeparam>
public abstract class PerEntryDocumentProjector<TState, TEntry, TDocument, TContext>
    : ICurrentStateProjectionMaterializer<TContext>
    where TState : class, IMessage<TState>, new()
    where TEntry : class
    where TDocument : class, IProjectionReadModel<TDocument>, IMessage<TDocument>, new()
    where TContext : class, IProjectionMaterializationContext
{
    private readonly IProjectionWriteDispatcher<TDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    /// <summary>
    /// Initializes one shared per-entry projector loop.
    /// </summary>
    protected PerEntryDocumentProjector(
        IProjectionWriteDispatcher<TDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async ValueTask ProjectAsync(
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<TState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        foreach (var entry in ExtractEntries(state) ?? [])
        {
            if (entry == null)
                continue;

            var key = EntryKey(entry);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            switch (Evaluate(entry))
            {
                case ProjectionVerdict.Project:
                    var document = Materialize(entry, context, stateEvent, updatedAt) ??
                                   throw new InvalidOperationException("Projector materialization returned null.");
                    if (!string.Equals(document.Id, key, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Projected document id '{document.Id}' does not match entry key '{key}'.");
                    }

                    await _writeDispatcher.UpsertAsync(document, ct);
                    break;
                case ProjectionVerdict.Skip:
                    break;
                case ProjectionVerdict.Tombstone:
                    await _writeDispatcher.DeleteAsync(key, ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(entry), "Unsupported projection verdict.");
            }
        }
    }

    /// <summary>
    /// Extracts every authoritative entry that should be considered by the projection loop,
    /// including tombstoned entries retained for watermark coordination.
    /// </summary>
    protected abstract IEnumerable<TEntry> ExtractEntries(TState state);

    /// <summary>
    /// Returns the stable read-model key for one authoritative entry.
    /// </summary>
    protected abstract string EntryKey(TEntry entry);

    /// <summary>
    /// Materializes one read-model document for the supplied entry.
    /// </summary>
    protected abstract TDocument Materialize(
        TEntry entry,
        TContext context,
        StateEvent stateEvent,
        DateTimeOffset updatedAt);

    /// <summary>
    /// Evaluates whether the supplied entry should be projected, skipped, or deleted from the read model.
    /// </summary>
    protected virtual ProjectionVerdict Evaluate(TEntry entry) => ProjectionVerdict.Project;
}
