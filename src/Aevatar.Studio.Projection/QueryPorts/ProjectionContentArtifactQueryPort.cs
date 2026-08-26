using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionContentArtifactQueryPort : IContentArtifactQueryPort
{
    public const int MaxPageSize = 200;

    private readonly IProjectionDocumentReader<ContentArtifactCurrentStateDocument, string> _documentReader;
    private readonly IContentArtifactBackingContentPort? _backingContentPort;

    public ProjectionContentArtifactQueryPort(
        IProjectionDocumentReader<ContentArtifactCurrentStateDocument, string> documentReader,
        IContentArtifactBackingContentPort? backingContentPort = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _backingContentPort = backingContentPort;
    }

    public async Task<ContentArtifactListResponse> ListAsync(
        string scopeId,
        string requesterPrincipalId,
        ContentArtifactQueryRequest query,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ContentArtifactConventions.NormalizeScopeId(scopeId);
        var normalizedRequesterPrincipalId = ContentArtifactConventions.NormalizeRequired(
            requesterPrincipalId,
            nameof(requesterPrincipalId));
        query ??= new ContentArtifactQueryRequest();
        var filters = new List<ProjectionDocumentFilter>
        {
            Equal("scope_id", normalizedScopeId),
        };
        AddOptionalEqual(filters, "team_id", query.TeamId);
        AddOptionalEqual(filters, "kind", query.Kind);
        AddOptionalEqual(filters, "lifecycle_status", query.LifecycleStatus);
        AddOptionalEqual(filters, "work_order_id", query.WorkOrderId);
        AddOptionalEqual(filters, "provenance_run_ids", query.RunId);
        if (query.LabelKey != null && query.LabelValue != null)
            filters.Add(Equal($"labels.{query.LabelKey}", query.LabelValue));
        var pageSize = query.PageSize is > 0 and <= MaxPageSize
            ? query.PageSize.Value
            : MaxPageSize;
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Filters = filters,
                AnyOfFilters =
                [
                    Equal("owner_principal_id", normalizedRequesterPrincipalId),
                    Equal("reader_principal_ids", normalizedRequesterPrincipalId),
                ],
                Take = pageSize,
                Cursor = NormalizeOptional(query.PageToken),
            },
            ct);

        return new ContentArtifactListResponse(
            normalizedScopeId,
            result.Items.Select(ToResponse).ToArray(),
            NormalizeOptional(result.NextCursor));
    }

    public async Task<ContentArtifactCurrentStateResponse?> GetAsync(
        string scopeId,
        string artifactId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ContentArtifactConventions.NormalizeScopeId(scopeId);
        var normalizedArtifactId = ContentArtifactConventions.NormalizeArtifactId(artifactId);
        var document = await _documentReader.GetAsync(
            ContentArtifactConventions.BuildActorId(normalizedScopeId, normalizedArtifactId),
            ct);
        if (document == null ||
            !string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
            !string.Equals(document.ArtifactId, normalizedArtifactId, StringComparison.Ordinal))
        {
            return null;
        }
        return ToResponse(document);
    }

    public Task<ContentArtifactCurrentStateResponse?> GetByDedupKeyAsync(
        string scopeId,
        string dedupKey,
        CancellationToken ct = default) =>
        GetAsync(
            scopeId,
            ContentArtifactConventions.BuildArtifactId(scopeId, dedupKey),
            ct);

    public async Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ContentArtifactConventions.NormalizeScopeId(scopeId);
        var normalizedArtifactId = ContentArtifactConventions.NormalizeArtifactId(artifactId);
        var normalizedRevisionId = ContentArtifactConventions.NormalizeRequired(revisionId, nameof(revisionId));
        var document = await _documentReader.GetAsync(
            ContentArtifactConventions.BuildActorId(normalizedScopeId, normalizedArtifactId),
            ct) ?? throw new ContentArtifactNotFoundException(normalizedScopeId, normalizedArtifactId);
        if (!string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
            !string.Equals(document.ArtifactId, normalizedArtifactId, StringComparison.Ordinal))
        {
            throw new ContentArtifactNotFoundException(normalizedScopeId, normalizedArtifactId);
        }
        EnsureReadAuthorized(document, requester);
        var revision = document.Revisions.FirstOrDefault(item =>
            string.Equals(item.RevisionId, normalizedRevisionId, StringComparison.Ordinal));
        if (revision == null)
            throw new ContentArtifactNotFoundException(normalizedScopeId, normalizedArtifactId);
        if (string.Equals(document.LifecycleStatus, ContentArtifactLifecycleStatusNames.Tombstoned, StringComparison.Ordinal))
        {
            throw new ContentArtifactContentUnavailableException(
                normalizedArtifactId,
                normalizedRevisionId,
                ContentArtifactContentUnavailableReason.Tombstoned);
        }
        if (revision.Availability is ContentArtifactRevisionAvailabilityNames.Redacted or
            ContentArtifactRevisionAvailabilityNames.RetentionExpired)
        {
            throw new ContentArtifactContentUnavailableException(
                normalizedArtifactId,
                normalizedRevisionId,
                revision.Availability == ContentArtifactRevisionAvailabilityNames.Redacted
                    ? ContentArtifactContentUnavailableReason.Redacted
                    : ContentArtifactContentUnavailableReason.RetentionExpired);
        }
        if (!string.Equals(revision.Availability, ContentArtifactRevisionAvailabilityNames.Available, StringComparison.Ordinal))
            throw new InvalidDataException("ContentArtifact revision availability is invalid.");

        var content = await ReadContentAsync(normalizedArtifactId, revision, ct);
        VerifyContent(normalizedArtifactId, revision, content);
        return new ContentArtifactRevisionContentResponse(
            new ContentArtifactReferenceContract(
                normalizedArtifactId,
                normalizedRevisionId,
                revision.ContentHash,
                revision.MediaType),
            content);
    }

    private async Task<byte[]> ReadContentAsync(
        string artifactId,
        ContentArtifactRevisionDocument revision,
        CancellationToken ct)
    {
        if (string.Equals(revision.ContentLocationKind, "inline", StringComparison.Ordinal))
            return revision.InlineContent.ToByteArray();
        if (!string.Equals(revision.ContentLocationKind, "backing_object", StringComparison.Ordinal))
            throw new InvalidDataException("ContentArtifact content location is unavailable.");
        if (_backingContentPort == null)
            throw new IOException("ContentArtifact backing content provider is unavailable.");

        try
        {
            await using var stream = await _backingContentPort.OpenReadAsync(
                new ContentArtifactBackingContentRequest(
                    new ContentArtifactBackingObjectReference
                    {
                        Provider = revision.BackingProvider,
                        ObjectKey = revision.BackingObjectKey,
                    },
                    revision.ProvenanceScopeId,
                    NormalizeOptional(revision.ProvenanceRunId)),
                ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (FileNotFoundException ex)
        {
            throw new IOException("ContentArtifact backing content is missing.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new IOException("ContentArtifact backing content is missing.", ex);
        }
    }

    private static void VerifyContent(
        string artifactId,
        ContentArtifactRevisionDocument revision,
        byte[] content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (content.LongLength != revision.ByteLength ||
            !string.Equals(hash, revision.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ContentArtifact '{artifactId}' revision '{revision.RevisionId}' content hash verification failed.");
        }
    }

    private static ContentArtifactCurrentStateResponse ToResponse(ContentArtifactCurrentStateDocument document) =>
        new(
            document.ArtifactId,
            document.ScopeId,
            NormalizeOptional(document.TeamId),
            document.Kind,
            document.Title,
            document.Classification,
            document.LifecycleStatus,
            NormalizeOptional(document.CurrentRevisionId),
            document.ConcurrencyVersion,
            document.StateVersion,
            new ContentArtifactPrincipalContract(document.OwnerPrincipalId, document.OwnerPrincipalKind),
            document.ReaderPrincipalIds.ToArray(),
            document.WriterPrincipalIds.ToArray(),
            string.IsNullOrWhiteSpace(document.RetentionPolicyId)
                ? null
                : new ContentArtifactRetentionPolicyContract(
                    document.RetentionPolicyId,
                    document.RetentionExpiresAtUtc?.ToDateTimeOffset()),
            NormalizeOptional(document.WorkOrderId),
            document.Revisions
                .OrderBy(static revision => revision.RevisionNumber)
                .Select(ToRevisionResponse)
                .ToArray(),
            document.CreatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            document.ArtifactUpdatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            NormalizeOptional(document.TombstoneReason),
            document.TombstonedAtUtc?.ToDateTimeOffset(),
            new Dictionary<string, string>(document.Labels, StringComparer.Ordinal));

    private static void EnsureReadAuthorized(
        ContentArtifactCurrentStateDocument document,
        ContentArtifactPrincipalContract requester)
    {
        ArgumentNullException.ThrowIfNull(requester);
        var principalId = ContentArtifactConventions.NormalizeRequired(
            requester.PrincipalId,
            "requester.principalId");
        _ = ContentArtifactConventions.NormalizeRequired(
            requester.PrincipalKind,
            "requester.principalKind");
        var isOwner = string.Equals(document.OwnerPrincipalId, principalId, StringComparison.Ordinal);
        if (!isOwner && !document.ReaderPrincipalIds.Contains(principalId))
            throw new ContentArtifactNotFoundException(document.ScopeId, document.ArtifactId);
    }

    private static ContentArtifactRevisionResponse ToRevisionResponse(ContentArtifactRevisionDocument revision) =>
        new(
            revision.RevisionId,
            revision.RevisionNumber,
            NormalizeOptional(revision.ParentRevisionId),
            revision.MediaType,
            revision.ByteLength,
            revision.ContentHash,
            revision.Availability,
            string.Equals(revision.ContentLocationKind, "inline", StringComparison.Ordinal),
            string.Equals(revision.ContentLocationKind, "backing_object", StringComparison.Ordinal),
            new ContentArtifactExecutionProvenanceContract(
                revision.ProvenanceScopeId,
                NormalizeOptional(revision.ProvenanceTeamId),
                NormalizeOptional(revision.ProvenanceMemberId),
                NormalizeOptional(revision.ProvenanceWorkflowId),
                NormalizeOptional(revision.ProvenancePublishedServiceId),
                NormalizeOptional(revision.ProvenanceRunId),
                NormalizeOptional(revision.ProvenanceWorkOrderId)),
            revision.Citations.Select(ToCitationResponse).ToArray(),
            revision.CreatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            NormalizeOptional(revision.RedactionReason),
            revision.RedactedAtUtc?.ToDateTimeOffset(),
            revision.RetentionExpiredAtUtc?.ToDateTimeOffset(),
            NormalizeOptional(revision.SupersessionReason));

    private static ContentArtifactCitationContract ToCitationResponse(ContentArtifactCitationDocument citation) =>
        new(
            citation.CitationId,
            NormalizeOptional(citation.Label),
            HasLocator(citation)
                ? new ContentArtifactCitationLocatorContract(
                    NormalizeOptional(citation.LocatorSection),
                    citation.LocatorStartOffset,
                    citation.LocatorEndOffset,
                    NormalizeOptional(citation.LocatorSelector))
                : null,
            string.IsNullOrWhiteSpace(citation.SourceArtifactId)
                ? null
                : new ContentArtifactReferenceContract(
                    citation.SourceArtifactId,
                    citation.SourceArtifactRevisionId,
                    citation.SourceArtifactContentHash,
                    citation.SourceArtifactMediaType),
            string.IsNullOrWhiteSpace(citation.ExternalSourceUri) &&
            string.IsNullOrWhiteSpace(citation.ExternalStableId)
                ? null
                : new ContentArtifactExternalCitationSourceContract(
                    NormalizeOptional(citation.ExternalSourceUri),
                    NormalizeOptional(citation.ExternalStableId),
                    NormalizeOptional(citation.ExternalDocumentRevision),
                    NormalizeOptional(citation.ExternalContentHash),
                    citation.ExternalPublishedAtUtc?.ToDateTimeOffset(),
                    citation.ExternalFetchedAtUtc?.ToDateTimeOffset()));

    private static bool HasLocator(ContentArtifactCitationDocument citation) =>
        !string.IsNullOrWhiteSpace(citation.LocatorSection) ||
        !string.IsNullOrWhiteSpace(citation.LocatorSelector) ||
        citation.LocatorStartOffset != 0 ||
        citation.LocatorEndOffset != 0;

    private static ProjectionDocumentFilter Equal(string fieldPath, string value) =>
        new()
        {
            FieldPath = fieldPath,
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(value),
        };

    private static void AddOptionalEqual(
        ICollection<ProjectionDocumentFilter> filters,
        string fieldPath,
        string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized != null)
            filters.Add(Equal(fieldPath, normalized));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
