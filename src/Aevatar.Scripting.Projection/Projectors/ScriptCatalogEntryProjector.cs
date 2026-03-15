using Aevatar.Scripting.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptCatalogEntryProjector
    : IProjectionProjector<ScriptAuthorityProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionDocumentReader<ScriptCatalogEntryDocument, string> _documentReader;
    private readonly IProjectionWriteDispatcher<ScriptCatalogEntryDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScriptCatalogEntryProjector(
        IProjectionDocumentReader<ScriptCatalogEntryDocument, string> documentReader,
        IProjectionWriteDispatcher<ScriptCatalogEntryDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
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

        var updatedAt = EventEnvelopeTimestampResolver.Resolve(envelope, _clock.UtcNow);
        if (envelope.Payload?.Is(ScriptCatalogRevisionPromotedEvent.Descriptor) == true)
        {
            var evt = envelope.Payload.Unpack<ScriptCatalogRevisionPromotedEvent>();
            if (string.IsNullOrWhiteSpace(evt.ScriptId))
                return;

            var documentId = BuildDocumentId(context.RootActorId, evt.ScriptId);
            var document = (await _documentReader.GetAsync(documentId, ct))?.DeepClone()
                           ?? new ScriptCatalogEntryDocument
                           {
                               Id = documentId,
                               CatalogActorId = context.RootActorId,
                               ScriptId = evt.ScriptId ?? string.Empty,
                               CreatedAt = updatedAt,
                           };
            document.CatalogActorId = context.RootActorId;
            document.ScriptId = evt.ScriptId ?? string.Empty;
            document.PreviousRevision = document.ActiveRevision ?? string.Empty;
            document.ActiveRevision = evt.Revision ?? string.Empty;
            document.ActiveDefinitionActorId = evt.DefinitionActorId ?? string.Empty;
            document.ActiveSourceHash = evt.SourceHash ?? string.Empty;
            document.LastProposalId = evt.ProposalId ?? string.Empty;
            document.UpdatedAt = updatedAt;
            document.LastEventId = string.Concat(document.ScriptId, ":", document.ActiveRevision, ":promoted");
            document.StateVersion += 1;
            if (!document.RevisionHistory.Any(x => string.Equals(x, document.ActiveRevision, StringComparison.Ordinal)))
                document.RevisionHistory.Add(document.ActiveRevision);
            await _writeDispatcher.UpsertAsync(document, ct);
            return;
        }

        if (envelope.Payload?.Is(ScriptCatalogRolledBackEvent.Descriptor) != true)
            return;

        var rollback = envelope.Payload.Unpack<ScriptCatalogRolledBackEvent>();
        if (string.IsNullOrWhiteSpace(rollback.ScriptId))
            return;

        var rollbackDocumentId = BuildDocumentId(context.RootActorId, rollback.ScriptId);
        var rollbackDocument = (await _documentReader.GetAsync(rollbackDocumentId, ct))?.DeepClone()
                               ?? new ScriptCatalogEntryDocument
                               {
                                   Id = rollbackDocumentId,
                                   CatalogActorId = context.RootActorId,
                                   ScriptId = rollback.ScriptId ?? string.Empty,
                                   CreatedAt = updatedAt,
                               };
        rollbackDocument.CatalogActorId = context.RootActorId;
        rollbackDocument.ScriptId = rollback.ScriptId ?? string.Empty;
        var previouslyActiveRevision = rollbackDocument.ActiveRevision ?? string.Empty;
        rollbackDocument.PreviousRevision = rollback.PreviousRevision ?? previouslyActiveRevision;
        rollbackDocument.ActiveRevision = rollback.TargetRevision ?? string.Empty;
        if (!string.Equals(rollbackDocument.ActiveRevision, previouslyActiveRevision, StringComparison.Ordinal))
        {
            rollbackDocument.ActiveDefinitionActorId = string.Empty;
            rollbackDocument.ActiveSourceHash = string.Empty;
        }

        rollbackDocument.LastProposalId = rollback.ProposalId ?? string.Empty;
        rollbackDocument.UpdatedAt = updatedAt;
        rollbackDocument.LastEventId = string.Concat(rollbackDocument.ScriptId, ":", rollbackDocument.ActiveRevision, ":rolled-back");
        rollbackDocument.StateVersion += 1;
        if (!rollbackDocument.RevisionHistory.Any(x => string.Equals(x, rollbackDocument.ActiveRevision, StringComparison.Ordinal)))
            rollbackDocument.RevisionHistory.Add(rollbackDocument.ActiveRevision);
        await _writeDispatcher.UpsertAsync(rollbackDocument, ct);
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
}
