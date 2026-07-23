using System.Globalization;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
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
            identity?.OwningScopeId ?? string.Empty,
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
        var failureCode = string.IsNullOrWhiteSpace(diagnostic?.Code)
            ? fallbackCode
            : diagnostic.Code;
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
