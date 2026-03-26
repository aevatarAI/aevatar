namespace Aevatar.GroupChat.Abstractions.Queries;

public sealed record GroupSourceCatalogSnapshot(
    string ActorId,
    string SourceId,
    GroupSourceKind SourceKind,
    string CanonicalLocator,
    GroupSourceAuthorityClass AuthorityClass,
    GroupSourceVerificationStatus VerificationStatus,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt);
