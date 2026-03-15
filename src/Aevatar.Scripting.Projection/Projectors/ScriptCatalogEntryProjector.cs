using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptCatalogEntryProjector
    : IProjectionProjector<ScriptAuthorityProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<ScriptCatalogEntryDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScriptCatalogEntryProjector(
        IProjectionWriteDispatcher<ScriptCatalogEntryDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(ScriptAuthorityProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        ScriptAuthorityProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ScriptCatalogState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var scriptId = ResolveScriptId(stateEvent.EventData);
        if (string.IsNullOrWhiteSpace(scriptId) ||
            !state.Entries.TryGetValue(scriptId, out var entry) ||
            entry == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var document = new ScriptCatalogEntryDocument
        {
            Id = BuildDocumentId(context.RootActorId, scriptId),
            CatalogActorId = context.RootActorId,
            ScriptId = string.IsNullOrWhiteSpace(entry.ScriptId) ? scriptId : entry.ScriptId,
            ActiveRevision = entry.ActiveRevision ?? string.Empty,
            ActiveDefinitionActorId = entry.ActiveDefinitionActorId ?? string.Empty,
            ActiveSourceHash = entry.ActiveSourceHash ?? string.Empty,
            PreviousRevision = entry.PreviousRevision ?? string.Empty,
            LastProposalId = entry.LastProposalId ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };
        document.RevisionHistory = entry.RevisionHistory.ToArray();
        await _writeDispatcher.UpsertAsync(document, ct);
    }

    public ValueTask CompleteAsync(
        ScriptAuthorityProjectionContext context,
        IReadOnlyList<string> projectionResult,
        CancellationToken ct = default)
    {
        _ = context;
        _ = projectionResult;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public static string BuildDocumentId(string catalogActorId, string scriptId) =>
        string.Concat(catalogActorId ?? string.Empty, ":", scriptId ?? string.Empty);

    private static string? ResolveScriptId(Any eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Is(ScriptCatalogRevisionPromotedEvent.Descriptor))
            return eventData.Unpack<ScriptCatalogRevisionPromotedEvent>().ScriptId;

        if (eventData.Is(ScriptCatalogRolledBackEvent.Descriptor))
            return eventData.Unpack<ScriptCatalogRolledBackEvent>().ScriptId;

        return null;
    }
}
