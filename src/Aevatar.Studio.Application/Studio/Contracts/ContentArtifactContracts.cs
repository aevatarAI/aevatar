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

public enum ContentArtifactContentUnavailableReason
{
    Unspecified = 0,
    Tombstoned = 1,
    Redacted = 2,
    RetentionExpired = 3,
    BackingUnavailable = 4,
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
    string? WorkOrderId = null,
    IReadOnlyDictionary<string, string>? Labels = null);

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
    DateTimeOffset? TombstonedAtUtc = null,
    IReadOnlyDictionary<string, string>? Labels = null);

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
    string? RunId = null,
    string? LabelKey = null,
    string? LabelValue = null);

public sealed record SetContentArtifactPinRequest(
    string ArtifactId,
    long ExpectedPinVersion,
    string MutationId);

public sealed record ClearContentArtifactPinRequest(
    long ExpectedPinVersion,
    string MutationId);

public sealed record ContentArtifactPinCurrentStateResponse(
    string ScopeId,
    string PinKey,
    string? PinnedArtifactId,
    ContentArtifactPrincipalContract? PinnedBy,
    long PinVersion,
    long StateVersion,
    DateTimeOffset UpdatedAtUtc,
    string LastMutationId,
    string LastMutationStatus,
    string? LastRejectionCode = null);

public sealed record ContentArtifactPinAcceptedReceipt(
    string ScopeId,
    string PinKey,
    string CommandId,
    string CorrelationId,
    string Stage,
    DateTimeOffset? AcceptedAtUtc = null);

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

public sealed class ContentArtifactPinNotFoundException : InvalidOperationException
{
    public ContentArtifactPinNotFoundException(string scopeId, string pinKey)
        : base($"ContentArtifact pin '{pinKey}' was not found in scope '{scopeId}'.")
    {
        ScopeId = scopeId;
        PinKey = pinKey;
    }

    public string ScopeId { get; }
    public string PinKey { get; }
}

public sealed class ContentArtifactContentUnavailableException : InvalidOperationException
{
    // Fix (review round 1, F4):
    //   Unavailable control flow was parsed from exception message text.
    //   The exception now carries a typed reason while preserving a useful message.
    public ContentArtifactContentUnavailableException(
        string artifactId,
        string revisionId,
        ContentArtifactContentUnavailableReason reason)
        : base($"ContentArtifact '{artifactId}' revision '{revisionId}' content is unavailable: {FormatReason(reason)}")
    {
        ArtifactId = artifactId;
        RevisionId = revisionId;
        Reason = reason;
    }

    public string ArtifactId { get; }
    public string RevisionId { get; }
    public ContentArtifactContentUnavailableReason Reason { get; }

    private static string FormatReason(ContentArtifactContentUnavailableReason reason) =>
        reason switch
        {
            ContentArtifactContentUnavailableReason.Tombstoned => "artifact is tombstoned",
            ContentArtifactContentUnavailableReason.Redacted => ContentArtifactRevisionAvailabilityNames.Redacted,
            ContentArtifactContentUnavailableReason.RetentionExpired => ContentArtifactRevisionAvailabilityNames.RetentionExpired,
            ContentArtifactContentUnavailableReason.BackingUnavailable => "backing content is unavailable",
            _ => "unspecified",
        };
}
