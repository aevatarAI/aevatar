namespace Aevatar.Studio.Application.Studio.Contracts;

public static class ContentArtifactCommandStageNames
{
    public const string DispatchAccepted = "dispatch_accepted";
}

public static class ContentArtifactLifecycleStatusNames
{
    public const string Active = "active";
    public const string Tombstoned = "tombstoned";
}

public static class ContentArtifactRevisionAvailabilityNames
{
    public const string Available = "available";
    public const string Redacted = "redacted";
    public const string RetentionExpired = "retention_expired";
}

public sealed record ContentArtifactPrincipalContract(
    string PrincipalId,
    string PrincipalKind);

public sealed record ContentArtifactReferenceContract(
    string ArtifactId,
    string RevisionId,
    string ContentHash,
    string MediaType);

public sealed record ContentArtifactAccessPolicyContract(
    IReadOnlyList<string>? ReaderPrincipalIds = null,
    IReadOnlyList<string>? WriterPrincipalIds = null);

public sealed record ContentArtifactRetentionPolicyContract(
    string PolicyId,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record ContentArtifactBackingObjectContract(
    string Provider,
    string ObjectKey);

public sealed record ContentArtifactExecutionProvenanceContract(
    string ScopeId,
    string? TeamId = null,
    string? MemberId = null,
    string? WorkflowId = null,
    string? PublishedServiceId = null,
    string? RunId = null,
    string? WorkOrderId = null);

public sealed record ContentArtifactCitationLocatorContract(
    string? Section = null,
    long? StartOffset = null,
    long? EndOffset = null,
    string? Selector = null);

public sealed record ContentArtifactExternalCitationSourceContract(
    string? SourceUri = null,
    string? StableExternalId = null,
    string? DocumentRevision = null,
    string? ContentHash = null,
    DateTimeOffset? PublishedAtUtc = null,
    DateTimeOffset? FetchedAtUtc = null);

public sealed record ContentArtifactCitationContract(
    string CitationId,
    string? Label = null,
    ContentArtifactCitationLocatorContract? Locator = null,
    ContentArtifactReferenceContract? ArtifactRevision = null,
    ContentArtifactExternalCitationSourceContract? ExternalSource = null);

public sealed record ContentArtifactRevisionWriteRequest(
    string DedupKey,
    string MediaType,
    string ContentHash,
    long ByteLength,
    ContentArtifactExecutionProvenanceContract Provenance,
    byte[]? InlineContent = null,
    ContentArtifactBackingObjectContract? BackingObject = null,
    string? ParentRevisionId = null,
    IReadOnlyList<ContentArtifactCitationContract>? Citations = null,
    string? SupersessionReason = null);

public sealed record CreateContentArtifactRequest(
    string? TeamId,
    string Kind,
    string Title,
    string Classification,
    string DedupKey,
    ContentArtifactRevisionWriteRequest FirstRevision,
    ContentArtifactAccessPolicyContract? AccessPolicy = null,
    ContentArtifactRetentionPolicyContract? RetentionPolicy = null,
    string? WorkOrderId = null);

public sealed record AppendContentArtifactRevisionRequest(
    ContentArtifactRevisionWriteRequest Revision);

public sealed record AdvanceContentArtifactCurrentRevisionRequest(
    long ExpectedConcurrencyVersion,
    string RevisionId);

public sealed record RedactContentArtifactRevisionRequest(
    long ExpectedConcurrencyVersion,
    string Reason);

public sealed record ExpireContentArtifactRevisionRequest(
    long ExpectedConcurrencyVersion);

public sealed record TombstoneContentArtifactRequest(
    long ExpectedConcurrencyVersion,
    string Reason);

public sealed record AttachContentArtifactsToRunRequest(
    string PublishedServiceId,
    string RunId,
    long ExpectedRunStateVersion,
    IReadOnlyList<ContentArtifactReferenceContract> Artifacts);

public sealed record ContentArtifactAcceptedReceipt(
    string ArtifactId,
    string CommandId,
    string CorrelationId,
    string Stage,
    DateTimeOffset? AcceptedAtUtc = null);

public sealed record ContentArtifactRevisionResponse(
    string RevisionId,
    long RevisionNumber,
    string? ParentRevisionId,
    string MediaType,
    long ByteLength,
    string ContentHash,
    string Availability,
    bool HasInlineContent,
    bool HasBackingContent,
    ContentArtifactExecutionProvenanceContract Provenance,
    IReadOnlyList<ContentArtifactCitationContract> Citations,
    DateTimeOffset CreatedAtUtc,
    string? RedactionReason = null,
    DateTimeOffset? RedactedAtUtc = null,
    DateTimeOffset? RetentionExpiredAtUtc = null,
    string? SupersessionReason = null);

public sealed record ContentArtifactCurrentStateResponse(
    string ArtifactId,
    string ScopeId,
    string? TeamId,
    string Kind,
    string Title,
    string Classification,
    string LifecycleStatus,
    string? CurrentRevisionId,
    long ConcurrencyVersion,
    long StateVersion,
    ContentArtifactPrincipalContract Owner,
    IReadOnlyList<string> ReaderPrincipalIds,
    IReadOnlyList<string> WriterPrincipalIds,
    ContentArtifactRetentionPolicyContract? RetentionPolicy,
    string? WorkOrderId,
    IReadOnlyList<ContentArtifactRevisionResponse> Revisions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? TombstoneReason = null,
    DateTimeOffset? TombstonedAtUtc = null);

public sealed record ContentArtifactListResponse(
    string ScopeId,
    IReadOnlyList<ContentArtifactCurrentStateResponse> Artifacts,
    string? NextPageToken = null);

public sealed record ContentArtifactQueryRequest(
    int? PageSize = null,
    string? PageToken = null,
    string? TeamId = null,
    string? Kind = null,
    string? LifecycleStatus = null,
    string? WorkOrderId = null,
    string? RunId = null);

public sealed record ContentArtifactRevisionContentResponse(
    ContentArtifactReferenceContract Reference,
    byte[] Content);

public sealed record ContentArtifactRunAttachmentReceipt(
    string RunId,
    string CommandId,
    string CorrelationId,
    string Stage,
    DateTimeOffset? AcceptedAtUtc = null);

public sealed class ContentArtifactNotFoundException : InvalidOperationException
{
    public ContentArtifactNotFoundException(string scopeId, string artifactId)
        : base($"ContentArtifact '{artifactId}' was not found in scope '{scopeId}'.")
    {
        ScopeId = scopeId;
        ArtifactId = artifactId;
    }

    public string ScopeId { get; }

    public string ArtifactId { get; }
}

public sealed class ContentArtifactIdentityConflictException : InvalidOperationException
{
    public ContentArtifactIdentityConflictException(string dedupKey)
        : base($"ContentArtifact dedup key '{dedupKey}' is already occupied in this scope.")
    {
        DedupKey = dedupKey;
    }

    public string DedupKey { get; }
}

public sealed class ContentArtifactContentUnavailableException : InvalidOperationException
{
    public ContentArtifactContentUnavailableException(string artifactId, string revisionId, string reason)
        : base($"ContentArtifact '{artifactId}' revision '{revisionId}' content is unavailable: {reason}")
    {
    }
}
