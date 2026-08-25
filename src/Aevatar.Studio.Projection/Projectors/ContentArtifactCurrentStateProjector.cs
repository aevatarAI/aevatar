using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class ContentArtifactCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ContentArtifactCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ContentArtifactCurrentStateProjector(
        IProjectionWriteDispatcher<ContentArtifactCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        if (!CommittedStateEventEnvelope.TryUnpackState<ContentArtifactState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null ||
            string.IsNullOrWhiteSpace(state.ArtifactId))
        {
            return;
        }

        await _writeDispatcher.UpsertAsync(
            ToDocument(
                context.RootActorId,
                stateEvent,
                state,
                CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)),
            ct);
    }

    public static ContentArtifactCurrentStateDocument ToDocument(
        string actorId,
        StateEvent stateEvent,
        ContentArtifactState state,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(stateEvent);
        ArgumentNullException.ThrowIfNull(state);
        var document = new ContentArtifactCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(observedAt),
            ArtifactId = state.ArtifactId,
            DedupKey = state.DedupKey,
            ScopeId = state.ScopeId,
            TeamId = state.TeamId,
            Kind = ToWireName(state.Kind),
            Title = state.Title,
            Classification = state.Classification,
            LifecycleStatus = ToWireName(state.LifecycleStatus),
            CurrentRevisionId = state.CurrentRevisionId,
            ConcurrencyVersion = state.ConcurrencyVersion,
            OwnerPrincipalId = state.AccessPolicy?.Owner?.PrincipalId ?? string.Empty,
            OwnerPrincipalKind = state.AccessPolicy?.Owner?.PrincipalKind ?? string.Empty,
            RetentionPolicyId = state.RetentionPolicy?.PolicyId ?? string.Empty,
            RetentionExpiresAtUtc = state.RetentionPolicy?.ExpiresAtUtc?.Clone(),
            WorkOrderId = state.WorkOrderId,
            CreatedAtUtc = state.CreatedAtUtc?.Clone(),
            ArtifactUpdatedAtUtc = state.UpdatedAtUtc?.Clone(),
            TombstoneReason = state.TombstoneReason,
            TombstonedAtUtc = state.TombstonedAtUtc?.Clone(),
        };
        if (state.AccessPolicy != null)
        {
            document.ReaderPrincipalIds.Add(state.AccessPolicy.ReaderPrincipalIds);
            document.WriterPrincipalIds.Add(state.AccessPolicy.WriterPrincipalIds);
        }
        document.Labels.Add(state.Labels);

        foreach (var revision in state.Revisions.Values.OrderBy(static item => item.RevisionNumber))
        {
            document.Revisions.Add(ToRevision(revision));
            if (!string.IsNullOrWhiteSpace(revision.Provenance?.RunId) &&
                !document.ProvenanceRunIds.Contains(revision.Provenance.RunId))
            {
                document.ProvenanceRunIds.Add(revision.Provenance.RunId);
            }
        }
        return document;
    }

    private static ContentArtifactRevisionDocument ToRevision(ContentArtifactRevision revision)
    {
        var document = new ContentArtifactRevisionDocument
        {
            RevisionId = revision.RevisionId,
            RevisionNumber = revision.RevisionNumber,
            ParentRevisionId = revision.ParentRevisionId,
            MediaType = revision.MediaType,
            ByteLength = revision.ByteLength,
            ContentHash = revision.ContentHash,
            Availability = ToWireName(revision.Availability),
            CreatedAtUtc = revision.CreatedAtUtc?.Clone(),
            RedactionReason = revision.RedactionReason,
            RedactedAtUtc = revision.RedactedAtUtc?.Clone(),
            RetentionExpiredAtUtc = revision.RetentionExpiredAtUtc?.Clone(),
            SupersessionReason = revision.SupersessionReason,
        };
        CopyRevisionContent(document, revision.Content);
        CopyRevisionProvenance(document, revision.Provenance);
        document.Citations.Add(revision.Citations.Select(ToCitation));
        return document;
    }

    private static void CopyRevisionContent(
        ContentArtifactRevisionDocument document,
        ContentArtifactRevisionContent? content)
    {
        document.ContentLocationKind = content?.LocationCase switch
        {
            ContentArtifactRevisionContent.LocationOneofCase.InlineContent => "inline",
            ContentArtifactRevisionContent.LocationOneofCase.BackingObject => "backing_object",
            _ => string.Empty,
        };
        document.InlineContent = content?.InlineContent ?? Google.Protobuf.ByteString.Empty;
        document.BackingProvider = content?.BackingObject?.Provider ?? string.Empty;
        document.BackingObjectKey = content?.BackingObject?.ObjectKey ?? string.Empty;
    }

    private static void CopyRevisionProvenance(
        ContentArtifactRevisionDocument document,
        ContentArtifactExecutionProvenance? provenance)
    {
        document.ProvenanceScopeId = provenance?.ScopeId ?? string.Empty;
        document.ProvenanceTeamId = provenance?.TeamId ?? string.Empty;
        document.ProvenanceMemberId = provenance?.MemberId ?? string.Empty;
        document.ProvenanceWorkflowId = provenance?.WorkflowId ?? string.Empty;
        document.ProvenancePublishedServiceId = provenance?.PublishedServiceId ?? string.Empty;
        document.ProvenanceRunId = provenance?.RunId ?? string.Empty;
        document.ProvenanceWorkOrderId = provenance?.WorkOrderId ?? string.Empty;
    }

    private static ContentArtifactCitationDocument ToCitation(ContentArtifactCitation citation)
    {
        var document = new ContentArtifactCitationDocument
        {
            CitationId = citation.CitationId,
            Label = citation.Label,
        };
        CopyCitationLocator(document, citation.Locator);
        CopyArtifactCitationSource(document, citation.ArtifactRevision?.Reference);
        CopyExternalCitationSource(document, citation.ExternalSource);
        return document;
    }

    private static void CopyCitationLocator(
        ContentArtifactCitationDocument document,
        ContentArtifactCitationLocator? locator)
    {
        document.LocatorSection = locator?.Section ?? string.Empty;
        document.LocatorStartOffset = locator?.StartOffset ?? 0;
        document.LocatorEndOffset = locator?.EndOffset ?? 0;
        document.LocatorSelector = locator?.Selector ?? string.Empty;
    }

    private static void CopyArtifactCitationSource(
        ContentArtifactCitationDocument document,
        ContentArtifactReference? reference)
    {
        document.SourceArtifactId = reference?.ArtifactId ?? string.Empty;
        document.SourceArtifactRevisionId = reference?.RevisionId ?? string.Empty;
        document.SourceArtifactContentHash = reference?.ContentHash ?? string.Empty;
        document.SourceArtifactMediaType = reference?.MediaType ?? string.Empty;
    }

    private static void CopyExternalCitationSource(
        ContentArtifactCitationDocument document,
        ContentArtifactExternalCitationSource? source)
    {
        document.ExternalSourceUri = source?.SourceUri ?? string.Empty;
        document.ExternalStableId = source?.StableExternalId ?? string.Empty;
        document.ExternalDocumentRevision = source?.DocumentRevision ?? string.Empty;
        document.ExternalContentHash = source?.ContentHash ?? string.Empty;
        document.ExternalPublishedAtUtc = source?.PublishedAtUtc?.Clone();
        document.ExternalFetchedAtUtc = source?.FetchedAtUtc?.Clone();
    }

    private static string ToWireName(ContentArtifactKind kind) => kind switch
    {
        ContentArtifactKind.Text => "text",
        ContentArtifactKind.Markdown => "markdown",
        ContentArtifactKind.StructuredDocument => "structured_document",
        ContentArtifactKind.OtherContent => "other_content",
        _ => string.Empty,
    };

    private static string ToWireName(ContentArtifactLifecycleStatus status) => status switch
    {
        ContentArtifactLifecycleStatus.Active => "active",
        ContentArtifactLifecycleStatus.Tombstoned => "tombstoned",
        _ => string.Empty,
    };

    private static string ToWireName(ContentArtifactRevisionAvailability availability) => availability switch
    {
        ContentArtifactRevisionAvailability.Available => "available",
        ContentArtifactRevisionAvailability.Redacted => "redacted",
        ContentArtifactRevisionAvailability.RetentionExpired => "retention_expired",
        _ => string.Empty,
    };
}
