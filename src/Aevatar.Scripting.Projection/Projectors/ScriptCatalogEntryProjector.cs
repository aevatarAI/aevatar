using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptCatalogEntryProjector
    : ICurrentStateProjectionMaterializer<ScriptAuthorityProjectionContext>
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
            state == null ||
            !IsCatalogMutation(stateEvent.EventData))
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        foreach (var (entryKey, entryValue) in state.Entries)
        {
            var entryScriptId = string.IsNullOrWhiteSpace(entryValue?.ScriptId)
                ? entryKey
                : entryValue.ScriptId;
            if (string.IsNullOrWhiteSpace(entryScriptId) || entryValue == null)
                continue;

            var document = new ScriptCatalogEntryDocument
            {
                Id = BuildDocumentId(context.RootActorId, entryScriptId),
                CatalogActorId = context.RootActorId,
                ScriptId = entryScriptId,
                ActiveRevision = entryValue.ActiveRevision ?? string.Empty,
                ActiveDefinitionActorId = entryValue.ActiveDefinitionActorId ?? string.Empty,
                ActiveSourceHash = entryValue.ActiveSourceHash ?? string.Empty,
                PreviousRevision = entryValue.PreviousRevision ?? string.Empty,
                LastProposalId = entryValue.LastProposalId ?? string.Empty,
                ScopeId = string.IsNullOrWhiteSpace(entryValue.ScopeId)
                    ? state.ScopeId ?? string.Empty
                    : entryValue.ScopeId,
                StateVersion = stateEvent.Version,
                LastEventId = stateEvent.EventId ?? string.Empty,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt,
            };
            document.RevisionHistory = entryValue.RevisionHistory.ToArray();
            await _writeDispatcher.UpsertAsync(document, ct);
        }
    }

    public static string BuildDocumentId(string catalogActorId, string scriptId) =>
        string.Concat(catalogActorId ?? string.Empty, ":", scriptId ?? string.Empty);

    private static bool IsCatalogMutation(Any eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Is(ScriptCatalogRevisionPromotedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptCatalogRollbackRequestedEvent.Descriptor))
            return true;

        if (eventData.Is(ScriptCatalogRolledBackEvent.Descriptor))
            return true;

        return false;
    }
}
