using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed record AgentProfileListPage(
    IReadOnlyList<AgentProfileCatalogEntry> Items,
    string? NextCursor,
    long AuthorityStateVersion,
    DateTimeOffset UpdatedAt,
    AgentProfileMutationOutcome? LastMutation = null);

public sealed record AgentProfileManagementDetail(
    AgentProfileManagementSnapshot Snapshot,
    string StrongETag,
    bool ExecutionAvailable = false)
{
    public AgentProfileIdentity Identity => Snapshot.Identity;
}

public sealed record AgentProfileValidationResult(
    bool IsValid,
    long DraftRevision,
    Google.Protobuf.ByteString DraftSha256,
    IReadOnlyList<AgentProfileSealingDiagnostic> Diagnostics);

public sealed record AgentProfileBindingDetail(
    AgentProfileDefaultBinding? Binding,
    long AuthorityStateVersion,
    string StrongETag,
    DateTimeOffset UpdatedAt,
    AgentProfileMutationOutcome? LastMutation = null);

public sealed record AgentProfileAcceptedReceipt(
    bool Accepted,
    string OperationId,
    string ProfileId,
    string CommandId,
    string CorrelationId,
    string ActorId,
    DateTimeOffset AcceptedAt);

public sealed record AgentProfileCreateRequest(
    AgentProfileOwner Owner,
    string ProfileSlug,
    string IdempotencyKey,
    string AuditSubject);

public sealed record AgentProfileDraftUpdateRequest(
    AgentProfileOwner Owner,
    string ProfileSlug,
    AgentProfileDraft Draft,
    long ExpectedAuthorityStateVersion,
    string IdempotencyKey,
    string AuditSubject);

public sealed record AgentProfilePublishRequest(
    AgentProfileOwner Owner,
    string ProfileSlug,
    long ExpectedAuthorityStateVersion,
    string IdempotencyKey,
    string AuditSubject,
    string? NyxIdAccessToken);

public sealed record AgentProfileBindingUpdateRequest(
    AgentProfileOwner Owner,
    string AgentKind,
    AgentProfileReference Reference,
    long ExpectedAuthorityStateVersion,
    string IdempotencyKey,
    string AuditSubject,
    bool Enabled = true,
    int CohortBasisPoints = AgentProfilePolicies.FullCohortBasisPoints);

public sealed record AgentProfileBindingClearRequest(
    AgentProfileOwner Owner,
    string AgentKind,
    long ExpectedAuthorityStateVersion,
    string IdempotencyKey,
    string AuditSubject);

public sealed class AgentProfileNotFoundException(string message) : InvalidOperationException(message);

public sealed class AgentProfileUnavailableException(string message) : InvalidOperationException(message);

public sealed class AgentProfileIntegrityException(string message) : InvalidOperationException(message);

public sealed class AgentProfileSealingException(
    IReadOnlyList<AgentProfileSealingDiagnostic> diagnostics)
    : InvalidOperationException("Agent Profile sealing rejected the current draft.")
{
    public IReadOnlyList<AgentProfileSealingDiagnostic> Diagnostics { get; } = diagnostics.ToArray();
}

public sealed class AgentProfileInvalidCursorException(string message) : ArgumentException(message);
