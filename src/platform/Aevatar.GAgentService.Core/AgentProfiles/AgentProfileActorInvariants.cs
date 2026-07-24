using System.Security.Cryptography;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Core.AgentProfiles;

public sealed class AgentProfileActorInvariantException : InvalidOperationException
{
    public AgentProfileActorInvariantException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}

internal static class AgentProfileActorInvariants
{
    public static AgentProfileOperationFact RequireOperation(AgentProfileOperationFact? operation)
    {
        if (operation is null ||
            !IsOpaque(operation.OperationId) ||
            !IsOpaque(operation.CommandId) ||
            !IsOpaque(operation.CorrelationId) ||
            operation.InputSha256.Length != 32)
        {
            throw Error("INVALID_PROFILE_OPERATION", "A complete typed Profile operation is required.");
        }

        return operation.Clone();
    }

    public static string RequireActorId(string? actorId, string path)
    {
        if (!IsOpaque(actorId))
            throw Error("INVALID_PROFILE_ACTOR_ID", $"A non-empty opaque Actor id is required at {path}.");
        return actorId!;
    }

    public static AgentProfileOperationReplayAuthority CanonicalReplayAuthority(
        AgentProfileOperationKind operationKind,
        string authorityActorId,
        string? counterpartyActorId,
        ByteString semanticInputSha256) =>
        CreateReplayAuthority(
            operationKind,
            authorityActorId,
            counterpartyActorId,
            semanticInputSha256,
            canonical: true);

    public static AgentProfileOperationReplayAuthority PrecanonicalReplayAuthority(
        AgentProfileOperationKind operationKind,
        string authorityActorId,
        string? counterpartyActorId,
        ByteString semanticInputSha256) =>
        CreateReplayAuthority(
            operationKind,
            authorityActorId,
            counterpartyActorId,
            semanticInputSha256,
            canonical: false);

    public static void EnsureSameReplayAuthority(
        AgentProfileOperationReplayAuthority? existing,
        AgentProfileOperationReplayAuthority candidate,
        string message)
    {
        if (!SameReplayAuthority(existing, candidate))
            throw Error("IDEMPOTENCY_PAYLOAD_CONFLICT", message);
    }

    public static bool SameReplayAuthority(
        AgentProfileOperationReplayAuthority? left,
        AgentProfileOperationReplayAuthority? right) =>
        left is not null &&
        right is not null &&
        left.OperationKind != AgentProfileOperationKind.Unspecified &&
        left.OperationKind == right.OperationKind &&
        string.Equals(left.AuthorityActorId, right.AuthorityActorId, StringComparison.Ordinal) &&
        string.Equals(left.CounterpartyActorId, right.CounterpartyActorId, StringComparison.Ordinal) &&
        left.SemanticFingerprintCase == right.SemanticFingerprintCase &&
        left.SemanticFingerprintCase switch
        {
            AgentProfileOperationReplayAuthority.SemanticFingerprintOneofCase
                .CanonicalSemanticInputSha256 =>
                DigestEquals(
                    left.CanonicalSemanticInputSha256,
                    right.CanonicalSemanticInputSha256),
            AgentProfileOperationReplayAuthority.SemanticFingerprintOneofCase
                .PrecanonicalSemanticInputSha256 =>
                DigestEquals(
                    left.PrecanonicalSemanticInputSha256,
                    right.PrecanonicalSemanticInputSha256),
            _ => false,
        };

    public static void RequireProtocolPublisher(
        string? publisherActorId,
        string expectedPublisherActorId)
    {
        var expected = RequireActorId(expectedPublisherActorId, "expected_publisher_actor_id");
        if (!string.Equals(publisherActorId, expected, StringComparison.Ordinal))
        {
            throw Error(
                "PROFILE_PROTOCOL_PUBLISHER_MISMATCH",
                "The Profile protocol envelope publisher does not match the expected Actor.");
        }
    }

    public static bool DigestEquals(ByteString? left, ByteString? right) =>
        left is not null &&
        right is not null &&
        left.Length == right.Length &&
        left.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(left.Span, right.Span);

    public static AgentProfileSafeDiagnostic InputDigestMismatch() =>
        Diagnostic(
            "OPERATION_INPUT_SHA256_MISMATCH",
            "The operation input digest does not match the normalized command payload.",
            "operation.input_sha256");

    public static AgentProfileSafeDiagnostic IdentityConflict() =>
        Diagnostic(
            "PROFILE_IDENTITY_CONFLICT",
            "Profile id, owner, scope, and human reference are immutable.",
            "identity");

    public static AgentProfileSafeDiagnostic VersionConflict() =>
        Diagnostic(
            "DRAFT_VERSION_CONFLICT",
            "The expected authoritative Profile version is stale.",
            "expected_authority_state_version");

    public static AgentProfileSafeDiagnostic PublishSourceChanged() =>
        Diagnostic(
            "PUBLISH_SOURCE_CHANGED",
            "The draft changed after the publish input was prepared.",
            "expected_draft_revision");

    public static AgentProfileSafeDiagnostic BindingConflict() =>
        Diagnostic(
            "PROFILE_BINDING_CONFLICT",
            "The sealed snapshot bindings do not exactly match the current draft.",
            "snapshot.skill_bindings");

    public static AgentProfileSafeDiagnostic MultipleDefaultSkills() =>
        Diagnostic(
            "MULTIPLE_DEFAULT_SKILLS",
            "Only one default-for-unmatched-turn skill is allowed.",
            "skill_bindings");

    public static AgentProfileSafeDiagnostic MissingBinding(string bindingId) =>
        Diagnostic(
            "PROFILE_BINDING_CONFLICT",
            $"Profile skill binding '{bindingId}' does not exist.",
            "binding_id");

    public static AgentProfileSafeDiagnostic SnapshotDigestMismatch() =>
        Diagnostic(
            "PUBLISHED_SNAPSHOT_SHA256_MISMATCH",
            "The sealed execution snapshot digest is invalid.",
            "snapshot.snapshot_sha256");

    public static AgentProfileSafeDiagnostic FirstDiagnostic(
        AgentProfileContractValidationException exception) =>
        exception.Diagnostics.Count > 0
            ? exception.Diagnostics[0].Clone()
            : Diagnostic("INVALID_AGENT_PROFILE", "Agent Profile validation failed.", string.Empty);

    public static AgentProfileCommittedStateTransition InitializationTransition(
        long draftRevision,
        ByteString draftSha256) =>
        new()
        {
            After = RevisionDigestFacts(
                draftRevision,
                draftSha256,
                publishedRevision: 0,
                publishedSnapshotSha256: ByteString.Empty),
        };

    public static AgentProfileMutationOutcome Outcome(
        AgentProfileState state,
        AgentProfileOperationFact operation,
        AgentProfileMutationStatus status,
        AgentProfileSafeDiagnostic? diagnostic = null,
        long? draftRevision = null,
        ByteString? draftSha256 = null,
        long? publishedRevision = null,
        ByteString? publishedSnapshotSha256 = null)
    {
        var before = RevisionDigestFacts(
            state.DraftRevision,
            state.DraftSha256,
            state.PublishedRevision,
            state.Published?.SnapshotSha256);
        var after = RevisionDigestFacts(
            draftRevision ?? before.DraftRevision,
            draftSha256 ?? before.DraftSha256,
            publishedRevision ?? before.PublishedRevision,
            publishedSnapshotSha256 ?? before.PublishedSnapshotSha256);
        var outcome = new AgentProfileMutationOutcome
        {
            Operation = operation.Clone(),
            Status = status,
            DraftRevision = after.DraftRevision,
            DraftSha256 = after.DraftSha256,
            PublishedRevision = after.PublishedRevision,
            PublishedSnapshotSha256 = after.PublishedSnapshotSha256,
            Transition = new AgentProfileCommittedStateTransition
            {
                Before = before,
                After = after,
            },
        };
        if (diagnostic is not null)
            outcome.Diagnostic = diagnostic.Clone();
        return outcome;
    }

    private static AgentProfileRevisionDigestFacts RevisionDigestFacts(
        long draftRevision,
        ByteString? draftSha256,
        long publishedRevision,
        ByteString? publishedSnapshotSha256) =>
        new()
        {
            DraftRevision = draftRevision,
            DraftSha256 = draftSha256 ?? ByteString.Empty,
            PublishedRevision = publishedRevision,
            PublishedSnapshotSha256 = publishedSnapshotSha256 ?? ByteString.Empty,
        };

    public static AgentProfilePublishedSummary Summary(AgentProfilePublishedSnapshot snapshot) =>
        new()
        {
            Reference = snapshot.Identity?.Reference?.Clone() ?? new AgentProfileReference(),
            DisplayName = snapshot.DisplayName ?? string.Empty,
            Purpose = snapshot.Purpose ?? string.Empty,
            PublishedRevision = snapshot.PublishedRevision,
            SnapshotSha256 = snapshot.SnapshotSha256,
        };

    public static bool SameIdentity(AgentProfileIdentity? left, AgentProfileIdentity? right) =>
        left is not null && right is not null && left.Equals(right);

    public static bool SameOwner(AgentProfileOwnerIdentity? left, AgentProfileOwnerIdentity? right) =>
        left is not null && right is not null && left.Equals(right);

    public static bool SameReference(AgentProfileReference? left, AgentProfileReference? right) =>
        left is not null && right is not null && left.Equals(right);

    public static bool SameSummary(
        AgentProfilePublishedSummary? left,
        AgentProfilePublishedSummary? right) =>
        left is not null && right is not null && left.Equals(right);

    public static bool HasAtMostOneDefaultBinding(AgentProfileContent content) =>
        content.SkillBindings.Count(static binding =>
            binding.ActivationMode == AgentProfileSkillActivationMode.DefaultForUnmatchedTurn) <= 1;

    public static AgentProfileSafeDiagnostic? ValidateSnapshotMatchesDraft(
        AgentProfilePublishedSnapshot snapshot,
        AgentProfileContent draft)
    {
        if (!string.Equals(snapshot.DisplayName, draft.DisplayName, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Purpose, draft.Purpose, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Instructions, draft.Instructions, StringComparison.Ordinal) ||
            snapshot.ToolPolicy?.Equals(draft.ToolPolicy) != true)
        {
            return PublishSourceChanged();
        }

        if (snapshot.SkillBindings.Count != draft.SkillBindings.Count)
            return BindingConflict();

        for (var index = 0; index < draft.SkillBindings.Count; index++)
        {
            var authored = draft.SkillBindings[index];
            var sealedBinding = snapshot.SkillBindings[index];
            if (!string.Equals(authored.BindingId, sealedBinding.BindingId, StringComparison.Ordinal) ||
                authored.ActivationMode != sealedBinding.ActivationMode ||
                sealedBinding.Skill?.ExactReference?.Equals(authored.Skill) != true)
            {
                return BindingConflict();
            }
        }

        return null;
    }

    public static AgentProfileSafeDiagnostic Diagnostic(string code, string message, string path) =>
        AgentProfilePolicies.NormalizeDiagnostic(new AgentProfileSafeDiagnostic
        {
            Code = code,
            Message = message,
            Path = path,
        });

    public static AgentProfileActorInvariantException Error(string code, string message) =>
        new(code, message);

    private static bool IsOpaque(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static AgentProfileOperationReplayAuthority CreateReplayAuthority(
        AgentProfileOperationKind operationKind,
        string authorityActorId,
        string? counterpartyActorId,
        ByteString semanticInputSha256,
        bool canonical)
    {
        if (operationKind == AgentProfileOperationKind.Unspecified)
            throw Error("INVALID_PROFILE_OPERATION_KIND", "A typed Profile operation kind is required.");

        var authority = RequireActorId(authorityActorId, "authority_actor_id");
        var counterparty = string.IsNullOrEmpty(counterpartyActorId)
            ? string.Empty
            : RequireActorId(counterpartyActorId, "counterparty_actor_id");
        if (semanticInputSha256 is null || semanticInputSha256.Length != 32)
        {
            throw Error(
                "INVALID_PROFILE_REPLAY_FINGERPRINT",
                "A SHA-256 Profile semantic replay fingerprint is required.");
        }

        var replayAuthority = new AgentProfileOperationReplayAuthority
        {
            OperationKind = operationKind,
            AuthorityActorId = authority,
            CounterpartyActorId = counterparty,
        };
        if (canonical)
            replayAuthority.CanonicalSemanticInputSha256 = semanticInputSha256;
        else
            replayAuthority.PrecanonicalSemanticInputSha256 = semanticInputSha256;
        return replayAuthority;
    }
}
