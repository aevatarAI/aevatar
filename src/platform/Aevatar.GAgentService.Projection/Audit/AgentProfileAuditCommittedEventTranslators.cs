using System.Globalization;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Projection.Audit;

public sealed class AgentProfileProvisioningStartedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileProvisioningStartedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileProvisioningStartedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileProvisioningStartedEvent evt) =>
        ProfileSeed(
            "agent_profile.provisioning.started",
            evt.Identity,
            evt.Operation,
            "provisioning",
            lifecyclePhase: AuditLifecyclePhase.Running,
            terminalOutcome: AuditTerminalOutcome.Unspecified);
}

public sealed class AgentProfileProvisioningCompletedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileProvisioningCompletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileProvisioningCompletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileProvisioningCompletedEvent evt) =>
        ProfileSeed(
            "agent_profile.provisioning.completed",
            evt.Identity,
            evt.Operation,
            "active");
}

public sealed class AgentProfileProvisioningFailedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileProvisioningFailedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileProvisioningFailedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileProvisioningFailedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["failure_kind"] = ProvisioningFailureKind(evt.FailureKind),
        };
        return ProfileFailureSeed(
            "agent_profile.provisioning.failed",
            evt.Identity,
            evt.Operation,
            "failed",
            evt.Diagnostic,
            "PROFILE_PROVISIONING_FAILED",
            annotations);
    }
}

public sealed class AgentProfilePublishedSummaryObservedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfilePublishedSummaryObservedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfilePublishedSummaryObservedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfilePublishedSummaryObservedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddPublishedSummaryFacts(annotations, evt.Summary);
        return ProfileSeed(
            "agent_profile.published_summary.observed",
            evt.Identity,
            evt.Operation,
            "observed",
            annotations);
    }
}

public sealed class AgentProfileInitializedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileInitializedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileInitializedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileInitializedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddDraftFacts(annotations, evt.DraftRevision, evt.DraftSha256);
        return ProfileSeed(
            "agent_profile.created",
            evt.Identity,
            evt.Operation,
            "applied",
            annotations);
    }
}

public sealed class AgentProfileInitializationRejectedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileInitializationRejectedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileInitializationRejectedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileInitializationRejectedEvent evt) =>
        ProfileFailureSeed(
            "agent_profile.initialization.rejected",
            evt.Identity,
            evt.Operation,
            "rejected",
            evt.Diagnostic,
            "PROFILE_INITIALIZATION_REJECTED");
}

public sealed class AgentProfileDraftUpdatedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileDraftUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileDraftUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileDraftUpdatedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddOutcomeFacts(annotations, evt.Outcome);
        AddDraftFacts(annotations, evt.DraftRevision, evt.DraftSha256);
        return ProfileSeed(
            "agent_profile.draft.updated",
            evt.Identity,
            evt.Operation,
            "applied",
            annotations);
    }
}

public sealed class AgentProfileSkillBindingUpsertedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileSkillBindingUpsertedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileSkillBindingUpsertedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileSkillBindingUpsertedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddOutcomeFacts(annotations, evt.Outcome);
        AddDraftFacts(annotations, evt.DraftRevision, evt.DraftSha256);
        AddBindingFacts(annotations, evt.Binding);
        return ProfileSeed(
            "agent_profile.skill_binding.upserted",
            evt.Identity,
            evt.Operation,
            "applied",
            annotations);
    }
}

public sealed class AgentProfileSkillBindingRemovedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileSkillBindingRemovedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileSkillBindingRemovedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileSkillBindingRemovedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["binding_id"] = evt.BindingId ?? string.Empty,
        };
        AddOutcomeFacts(annotations, evt.Outcome);
        AddDraftFacts(annotations, evt.DraftRevision, evt.DraftSha256);
        return ProfileSeed(
            "agent_profile.skill_binding.removed",
            evt.Identity,
            evt.Operation,
            "applied",
            annotations,
            isDestructive: true);
    }
}

public sealed class AgentProfilePublishedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfilePublishedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfilePublishedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfilePublishedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddOutcomeFacts(annotations, evt.Outcome);
        AddPublishedSnapshotFacts(annotations, evt.Snapshot);
        return ProfileSeed(
            "agent_profile.published",
            evt.Identity,
            evt.Operation,
            "applied",
            annotations);
    }
}

public sealed class AgentProfilePublishNoChangeAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfilePublishNoChangeEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfilePublishNoChangeEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfilePublishNoChangeEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddOutcomeFacts(annotations, evt.Outcome);
        AddPublishedSummaryFacts(annotations, evt.Summary);
        return ProfileSeed(
            "agent_profile.publish.no_change",
            evt.Identity,
            evt.Operation,
            "no_change",
            annotations);
    }
}

public sealed class AgentProfileMutationNoChangeAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileMutationNoChangeEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileMutationNoChangeEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileMutationNoChangeEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddOutcomeFacts(annotations, evt.Outcome);
        return ProfileSeed(
            "agent_profile.mutation.no_change",
            evt.Identity,
            evt.Operation,
            "no_change",
            annotations);
    }
}

public sealed class AgentProfileMutationRejectedAuditTranslator
    : AgentProfileAuditTranslatorBase<AgentProfileMutationRejectedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileMutationRejectedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileMutationRejectedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        AddOutcomeFacts(annotations, evt.Outcome);
        return ProfileFailureSeed(
            "agent_profile.mutation.rejected",
            evt.Identity,
            evt.Operation,
            "rejected",
            evt.Outcome?.Diagnostic,
            "PROFILE_MUTATION_REJECTED",
            annotations);
    }
}

public abstract class AgentProfileAuditTranslatorBase<TEvent> : AuditTranslatorBase<TEvent>
    where TEvent : class, IMessage<TEvent>, new()
{
    protected static CommittedAuditSeed ProfileSeed(
        string operationName,
        AgentProfileIdentity? identity,
        AgentProfileOperationFact? operation,
        string outcomeCode,
        IReadOnlyDictionary<string, string>? annotations = null,
        bool isDestructive = false,
        AuditLifecyclePhase lifecyclePhase = AuditLifecyclePhase.Terminal,
        AuditTerminalOutcome terminalOutcome = AuditTerminalOutcome.Succeeded,
        AuditFailure? failure = null)
    {
        var merged = IdentityAnnotations(identity);
        merged["operation_id"] = operation?.OperationId ?? string.Empty;
        merged["outcome_code"] = outcomeCode;
        if (annotations is not null)
        {
            foreach (var pair in annotations)
                merged[pair.Key] = pair.Value ?? string.Empty;
        }

        return new CommittedAuditSeed(
            operationName,
            "agent_profile",
            identity?.ProfileId ?? string.Empty,
            AuditScope(identity),
            AuditSensitivityLevel.Restricted,
            isDestructive,
            operation?.CommandId ?? string.Empty,
            CorrelationId: operation?.CorrelationId ?? string.Empty,
            ResultSummary: "Agent Profile committed fact recorded.",
            Annotations: merged,
            LifecyclePhase: lifecyclePhase,
            TerminalOutcome: terminalOutcome,
            Failure: failure);
    }

    protected static CommittedAuditSeed ProfileFailureSeed(
        string operationName,
        AgentProfileIdentity? identity,
        AgentProfileOperationFact? operation,
        string outcomeCode,
        AgentProfileSafeDiagnostic? diagnostic,
        string fallbackCode,
        IReadOnlyDictionary<string, string>? annotations = null)
    {
        var failureCode = StableFailureCode(diagnostic?.Code, fallbackCode);
        var merged = annotations is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(annotations, StringComparer.Ordinal);
        merged["failure_code"] = failureCode;
        return ProfileSeed(
            operationName,
            identity,
            operation,
            outcomeCode,
            merged,
            lifecyclePhase: AuditLifecyclePhase.Terminal,
            terminalOutcome: AuditTerminalOutcome.Failed,
            failure: new AuditFailure
            {
                Code = failureCode,
                Category = FailureCategory(failureCode),
                Retryability = AuditRetryability.Unknown,
                FailedPhase = AuditLifecyclePhase.Running,
                SanitizedMessage = failureCode,
            });
    }

    protected static void AddDraftFacts(
        IDictionary<string, string> annotations,
        long draftRevision,
        ByteString? draftSha256)
    {
        if (draftRevision > 0)
            annotations["draft_revision"] = draftRevision.ToString(CultureInfo.InvariantCulture);
        AddDigest(annotations, "draft_sha256", draftSha256);
    }

    protected static void AddOutcomeFacts(
        IDictionary<string, string> annotations,
        AgentProfileMutationOutcome? outcome)
    {
        if (outcome is null)
            return;

        AddDraftFacts(annotations, outcome.DraftRevision, outcome.DraftSha256);
        if (outcome.PublishedRevision > 0)
        {
            annotations["published_revision"] =
                outcome.PublishedRevision.ToString(CultureInfo.InvariantCulture);
        }
        AddDigest(
            annotations,
            "published_snapshot_sha256",
            outcome.PublishedSnapshotSha256);
    }

    protected static void AddBindingFacts(
        IDictionary<string, string> annotations,
        AgentProfileSkillBinding? binding)
    {
        if (binding is null)
            return;

        annotations["binding_id"] = binding.BindingId ?? string.Empty;
        annotations["activation_mode"] = ActivationMode(binding.ActivationMode);
        if (binding.Skill is null)
            return;

        annotations["skill_guid"] = binding.Skill.SkillGuid ?? string.Empty;
        annotations["literal_version"] = binding.Skill.LiteralVersion ?? string.Empty;
        annotations["expected_name"] = binding.Skill.ExpectedName ?? string.Empty;
        annotations["expected_publisher_id"] = binding.Skill.ExpectedPublisherId ?? string.Empty;
    }

    protected static void AddPublishedSummaryFacts(
        IDictionary<string, string> annotations,
        AgentProfilePublishedSummary? summary)
    {
        if (summary is null)
            return;

        if (summary.PublishedRevision > 0)
        {
            annotations["published_revision"] =
                summary.PublishedRevision.ToString(CultureInfo.InvariantCulture);
        }
        AddDigest(annotations, "published_snapshot_sha256", summary.SnapshotSha256);
    }

    protected static void AddPublishedSnapshotFacts(
        IDictionary<string, string> annotations,
        AgentProfilePublishedSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        if (snapshot.PublishedRevision > 0)
        {
            annotations["published_revision"] =
                snapshot.PublishedRevision.ToString(CultureInfo.InvariantCulture);
        }
        AddDigest(
            annotations,
            "published_source_draft_sha256",
            snapshot.SourceDraftSha256);
        AddDigest(annotations, "published_snapshot_sha256", snapshot.SnapshotSha256);
    }

    private static Dictionary<string, string> IdentityAnnotations(AgentProfileIdentity? identity)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile_id"] = identity?.ProfileId ?? string.Empty,
            ["owner_kind"] = OwnerKind(identity?.Owner),
            ["owner_handle"] = identity?.Reference?.OwnerHandle ?? string.Empty,
            ["profile_slug"] = identity?.Reference?.ProfileSlug ?? string.Empty,
        };
        return annotations;
    }

    private static string OwnerKind(AgentProfileOwnerIdentity? owner) => owner?.OwnerCase switch
    {
        AgentProfileOwnerIdentity.OwnerOneofCase.User => "user",
        AgentProfileOwnerIdentity.OwnerOneofCase.System => "system",
        _ => "unspecified",
    };

    private static string AuditScope(AgentProfileIdentity? identity)
    {
        if (identity is null || AgentProfilePolicies.ValidateIdentity(identity).Count > 0)
            return AuditContractSemantics.PlatformAuditScopeId;

        return identity.Owner?.OwnerCase switch
        {
            AgentProfileOwnerIdentity.OwnerOneofCase.User => identity.OwningScopeId ?? string.Empty,
            AgentProfileOwnerIdentity.OwnerOneofCase.System when
                string.Equals(
                    identity.Owner.System?.PlatformId,
                    AgentProfilePolicies.AevatarPlatformId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    identity.Reference?.OwnerHandle,
                    AgentProfilePolicies.SystemOwnerHandle,
                    StringComparison.Ordinal) &&
                string.IsNullOrEmpty(identity.OwningScopeId) =>
                AuditContractSemantics.PlatformAuditScopeId,
            _ => AuditContractSemantics.PlatformAuditScopeId,
        };
    }

    private static string StableFailureCode(string? candidate, string fallbackCode) =>
        candidate is
            "AGGREGATE_PROMPT_BYTES_EXCEEDED" or
            "AGGREGATE_PROMPT_TOKENS_EXCEEDED" or
            "CONFLICTING_ASSET_PATH" or
            "CONFLICTING_SCRIPT_ID" or
            "CONFLICTING_WORKFLOW_ID" or
            "DRAFT_VERSION_CONFLICT" or
            "DUPLICATE_BINDING_ID" or
            "IDEMPOTENCY_PAYLOAD_CONFLICT" or
            "INVALID_AGENT_PROFILE" or
            "INVALID_ASSET_PATH" or
            "INVALID_BINDING_ID" or
            "INVALID_DECLARED_TOOL_NAME" or
            "INVALID_DISPLAY_NAME" or
            "INVALID_EXPECTED_PUBLISHER_ID" or
            "INVALID_EXPECTED_SKILL_NAME" or
            "INVALID_IDENTITY_PROVIDER" or
            "INVALID_INSTRUCTIONS" or
            "INVALID_LITERAL_VERSION" or
            "INVALID_OWNER_HANDLE" or
            "INVALID_OWNER_SUBJECT_ID" or
            "INVALID_OWNING_SCOPE_ID" or
            "INVALID_PROFILE_ACTOR_ID" or
            "INVALID_PROFILE_ID" or
            "INVALID_PROFILE_INITIALIZATION_IDENTITY" or
            "INVALID_PROFILE_INITIALIZATION_REJECTION" or
            "INVALID_PROFILE_OPERATION" or
            "INVALID_PROFILE_OPERATION_KIND" or
            "INVALID_PROFILE_OWNER" or
            "INVALID_PROFILE_REPLAY_FINGERPRINT" or
            "INVALID_PROFILE_SLUG" or
            "INVALID_PUBLISHED_REVISION" or
            "INVALID_PUBLISHED_SNAPSHOT_SHA256" or
            "INVALID_PURPOSE" or
            "INVALID_SCRIPT_ID" or
            "INVALID_SEALED_SKILL_BINDING" or
            "INVALID_SKILL_ACTIVATION_MODE" or
            "INVALID_SKILL_GUID" or
            "INVALID_SKILL_PACKAGE" or
            "INVALID_SYSTEM_PLATFORM_ID" or
            "INVALID_SYSTEM_PROFILE_REFERENCE" or
            "INVALID_TOOL_NAME" or
            "INVALID_TOOL_POLICY_MODE" or
            "INVALID_TOOL_SET_REF" or
            "INVALID_WORKFLOW_ID" or
            "MISSING_INITIALIZATION_CONTINUATION" or
            "MISSING_INITIALIZATION_REJECTION" or
            "MISSING_PROFILE_CONTENT" or
            "MISSING_PROFILE_IDENTITY" or
            "MISSING_PROFILE_OWNER" or
            "MISSING_PROFILE_REFERENCE" or
            "MISSING_PUBLISHED_SNAPSHOT" or
            "MISSING_RESOLVED_SKILL_PACKAGE" or
            "MISSING_SEALED_SKILL" or
            "MISSING_SKILL_BINDING" or
            "MISSING_SKILL_REFERENCE" or
            "MISSING_TOOL_POLICY" or
            "MISSING_UPSTREAM_SKILL_HASH" or
            "MULTIPLE_DEFAULT_SKILLS" or
            "OPERATION_INPUT_SHA256_MISMATCH" or
            "ORNN_ACCESS_TOKEN_REQUIRED" or
            "ORNN_DEPENDENCY_UNAVAILABLE" or
            "ORNN_SKILL_ACCESS_DENIED" or
            "ORNN_SKILL_IDENTITY_MISMATCH" or
            "ORNN_SKILL_NOT_FOUND" or
            "ORNN_SKILL_PUBLISHER_MISMATCH" or
            "OWNER_HANDLE_CONFLICT" or
            "PROFILE_ACTOR_ID_TAKEN" or
            "PROFILE_BINDING_CONFLICT" or
            "PROFILE_IDENTITY_CONFLICT" or
            "PROFILE_ID_TAKEN" or
            "PROFILE_NOT_INITIALIZED" or
            "PROFILE_PROTOCOL_PUBLISHER_MISMATCH" or
            "PROFILE_PROVISIONING_CONTINUATION_MISMATCH" or
            "PROFILE_PUBLISHED_SUMMARY_MISMATCH" or
            "PROFILE_SLUG_TAKEN" or
            "PUBLISHED_SNAPSHOT_SHA256_MISMATCH" or
            "PUBLISHED_SNAPSHOT_TOO_LARGE" or
            "PUBLISH_SOURCE_CHANGED" or
            "RESERVED_OWNER_HANDLE" or
            "SEALED_SKILL_CANONICAL_NAME_MISMATCH" or
            "SEALED_SKILL_CONTENT_SHA256_MISMATCH" or
            "SEALED_SKILL_GUID_MISMATCH" or
            "SEALED_SKILL_LITERAL_VERSION_MISMATCH" or
            "SEALED_SKILL_PUBLISHER_ID_MISMATCH" or
            "SEALED_SKILL_TOO_LARGE" or
            "SKILL_TOOL_DEPENDENCY_NOT_ALLOWED" or
            "SYSTEM_PROFILE_SCOPE_FORBIDDEN" or
            "TEXT_ASSET_TOO_LARGE" or
            "TOO_MANY_SKILL_BINDINGS" or
            "TOO_MANY_TOOL_NAMES" or
            "TOO_MANY_TOOL_SET_REFS" or
            "UNKNOWN_PROFILE_PROVISIONING" or
            "UNKNOWN_TOOL_SET_REF"
            ? candidate
            : fallbackCode;

    private static string ActivationMode(AgentProfileSkillActivationMode mode) => mode switch
    {
        AgentProfileSkillActivationMode.Always => "always",
        AgentProfileSkillActivationMode.Routed => "routed",
        AgentProfileSkillActivationMode.DefaultForUnmatchedTurn => "default_for_unmatched_turn",
        _ => "unspecified",
    };

    protected static string ProvisioningFailureKind(AgentProfileProvisioningFailureKind kind) => kind switch
    {
        AgentProfileProvisioningFailureKind.CreateValidation => "create_validation",
        AgentProfileProvisioningFailureKind.InitializationContinuation => "initialization_continuation",
        _ => "unspecified",
    };

    private static AuditFailureCategory FailureCategory(string code)
    {
        if (code.Contains("CONFLICT", StringComparison.Ordinal))
            return AuditFailureCategory.Conflict;
        if (code.StartsWith("ORNN_", StringComparison.Ordinal))
            return AuditFailureCategory.Dependency;
        if (code.Contains("INVALID", StringComparison.Ordinal) ||
            code.Contains("MISSING", StringComparison.Ordinal) ||
            code.Contains("REQUIRED", StringComparison.Ordinal))
        {
            return AuditFailureCategory.Validation;
        }

        return AuditFailureCategory.Execution;
    }

    private static void AddDigest(
        IDictionary<string, string> annotations,
        string key,
        ByteString? digest)
    {
        if (digest is not { Length: > 0 })
            return;

        annotations[key] = Convert.ToHexString(digest.Span).ToLowerInvariant();
    }
}
