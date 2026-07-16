using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled.Audit;

// Committed-fact audit translators for the scheduled-agent lifecycle / authorization
// events. Both owning actors carry opaque, non-subject ids: the user-agent catalog
// is a singleton scope-store actor ("agent-registry-store") and each skill runner is
// keyed by a generated "skill-runner-{guid}" actor id (SkillRunnerDefaults.GenerateActorId).
// Neither embeds a raw external subject, so the plain base is correct.
//
// Security boundary (docs/canon/audit-trail.md §4): the catalog entry and skill runner
// outbound config carry a NyxID api key / credential (UserAgentCatalogEntry.nyx_api_key,
// SkillRunnerOutboundConfig.nyx_api_key). Those are credentials and MUST NOT enter the
// audit artifact. Only ids, scopes, kinds, and safe labels are recorded. External-trigger
// payload_summary is likewise excluded because it can carry raw inbound message content.

public sealed class UserAgentCatalogUpsertedAuditTranslator
    : ScheduledAuditTranslatorBase<UserAgentCatalogUpsertedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(UserAgentCatalogUpsertedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        UserAgentCatalogUpsertedEvent evt)
    {
        var entry = evt.Entry;
        return ScheduledSeed(
            "scheduled.user-agent-catalog.upserted",
            "user_agent_catalog",
            entry?.AgentId ?? string.Empty,
            entry?.ScopeId ?? string.Empty,
            "User agent catalog entry upserted.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agent_type"] = entry?.AgentType ?? string.Empty,
                ["template_name"] = entry?.TemplateName ?? string.Empty,
                ["nyx_provider_slug"] = entry?.NyxProviderSlug ?? string.Empty,
                ["target_platform"] = entry?.TargetPlatform ?? string.Empty,
            });
    }
}

public sealed class UserAgentCatalogTombstonedAuditTranslator
    : ScheduledAuditTranslatorBase<UserAgentCatalogTombstonedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(UserAgentCatalogTombstonedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        UserAgentCatalogTombstonedEvent evt) =>
        ScheduledSeed(
            "scheduled.user-agent-catalog.tombstoned",
            "user_agent_catalog",
            evt.AgentId,
            string.Empty,
            "User agent catalog entry tombstoned.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class UserAgentCatalogSharedAuditTranslator
    : ScheduledAuditTranslatorBase<UserAgentCatalogSharedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(UserAgentCatalogSharedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        UserAgentCatalogSharedEvent evt)
    {
        var grant = evt.SharingGrant;
        return ScheduledSeed(
            "scheduled.user-agent-catalog.shared",
            "user_agent_catalog",
            evt.AgentId,
            string.Empty,
            "User agent catalog entry shared to another registration scope.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shared_with_registration_scope"] = grant?.SharedWithRegistrationScope ?? string.Empty,
                ["allow_trigger"] = (grant?.AllowTrigger ?? false) ? "true" : "false",
            });
    }
}

public sealed class UserAgentCatalogUnsharedAuditTranslator
    : ScheduledAuditTranslatorBase<UserAgentCatalogUnsharedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(UserAgentCatalogUnsharedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        UserAgentCatalogUnsharedEvent evt) =>
        ScheduledSeed(
            "scheduled.user-agent-catalog.unshared",
            "user_agent_catalog",
            evt.AgentId,
            string.Empty,
            "User agent catalog entry sharing grant revoked.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true);
}

public sealed class SkillRunnerInitializedAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerInitializedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerInitializedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerInitializedEvent evt) =>
        ScheduledSeed(
            "scheduled.skill-runner.initialized",
            "skill_runner",
            context.OriginActorId,
            evt.ScopeId,
            "Skill runner initialized.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["skill_name"] = evt.SkillName ?? string.Empty,
                ["template_name"] = evt.TemplateName ?? string.Empty,
                ["schedule_mode"] = evt.ScheduleMode.ToString(),
                ["enabled"] = evt.Enabled ? "true" : "false",
            });
}

public sealed class SkillRunnerEnabledAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerEnabledEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerEnabledEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerEnabledEvent evt) =>
        ScheduledSeed(
            "scheduled.skill-runner.enabled",
            "skill_runner",
            context.OriginActorId,
            string.Empty,
            "Skill runner enabled.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = evt.Reason ?? string.Empty,
            });
}

public sealed class SkillRunnerDisabledAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerDisabledEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerDisabledEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerDisabledEvent evt) =>
        ScheduledSeed(
            "scheduled.skill-runner.disabled",
            "skill_runner",
            context.OriginActorId,
            string.Empty,
            "Skill runner disabled.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true,
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = evt.Reason ?? string.Empty,
            });
}

public sealed class SkillRunnerOneShotRetiredAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerOneShotRetiredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerOneShotRetiredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerOneShotRetiredEvent evt) =>
        ScheduledSeed(
            "scheduled.skill-runner.one-shot.retired",
            "skill_runner",
            context.OriginActorId,
            string.Empty,
            "Skill runner one-shot retired.",
            AuditSensitivityLevel.Restricted,
            isDestructive: true,
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = evt.Reason ?? string.Empty,
            });
}

public sealed class SkillRunnerExternalTriggerAdmittedAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerExternalTriggerAdmittedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerExternalTriggerAdmittedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerExternalTriggerAdmittedEvent evt)
    {
        var identity = evt.Identity;
        return ScheduledSeed(
            "scheduled.skill-runner.external-trigger.admitted",
            "skill_runner",
            context.OriginActorId,
            string.Empty,
            "Skill runner external trigger admitted.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_id"] = identity?.SourceId ?? string.Empty,
                ["delivery_id"] = identity?.DeliveryId ?? string.Empty,
                ["admission_id"] = identity?.AdmissionId ?? string.Empty,
                ["trigger_kind"] = identity?.Kind.ToString() ?? string.Empty,
            });
    }
}

public sealed class SkillRunnerExternalTriggerRejectedAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerExternalTriggerRejectedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerExternalTriggerRejectedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerExternalTriggerRejectedEvent evt)
    {
        var identity = evt.Identity;
        return ScheduledSeed(
            "scheduled.skill-runner.external-trigger.rejected",
            "skill_runner",
            context.OriginActorId,
            string.Empty,
            "Skill runner external trigger rejected.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_id"] = identity?.SourceId ?? string.Empty,
                ["delivery_id"] = identity?.DeliveryId ?? string.Empty,
                ["trigger_kind"] = identity?.Kind.ToString() ?? string.Empty,
                ["reason"] = evt.Reason ?? string.Empty,
            });
    }
}

public sealed class SkillRunnerExecutionCompletedAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerExecutionCompletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerExecutionCompletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerExecutionCompletedEvent evt) =>
        // The `output` field carries the full skill/LLM output body and MUST NOT be
        // recorded (docs/canon/audit-trail.md §4). Only the terminal status and the
        // safe execution labels/ids are captured.
        ScheduledSeed(
            "scheduled.skill-runner.execution.completed",
            "skill_runner",
            context.OriginActorId,
            string.Empty,
            "Skill runner execution completed.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = "succeeded",
                ["execution_kind"] = evt.ExecutionKind.ToString(),
                ["skill_name"] = evt.SkillName ?? string.Empty,
                ["skill_version"] = evt.SkillVersion ?? string.Empty,
                ["workflow_id"] = evt.WorkflowId ?? string.Empty,
                ["cron_occurrence_key"] = evt.CronOccurrenceKey ?? string.Empty,
            });
}

public sealed class SkillRunnerExecutionFailedAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerExecutionFailedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerExecutionFailedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerExecutionFailedEvent evt) =>
        // The `error` field is free-text and can carry business payload, so only the
        // typed error CLASS (error_code) is recorded, never the full error body.
        ScheduledSeed(
            "scheduled.skill-runner.execution.failed",
            "skill_runner",
            context.OriginActorId,
            string.Empty,
            "Skill runner execution failed.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = "failed",
                ["error_code"] = evt.ErrorCode.ToString(),
                ["execution_kind"] = evt.ExecutionKind.ToString(),
                ["skill_name"] = evt.SkillName ?? string.Empty,
                ["skill_version"] = evt.SkillVersion ?? string.Empty,
                ["workflow_id"] = evt.WorkflowId ?? string.Empty,
                ["cron_occurrence_key"] = evt.CronOccurrenceKey ?? string.Empty,
            });
}

public sealed class SkillRunnerExecutionRejectedAuditTranslator
    : ScheduledAuditTranslatorBase<SkillRunnerExecutionRejectedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(SkillRunnerExecutionRejectedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        SkillRunnerExecutionRejectedEvent evt) =>
        // `reason` is a short admission/rejection reason label (not a payload body).
        ScheduledSeed(
            "scheduled.skill-runner.execution.rejected",
            "skill_runner",
            context.OriginActorId,
            string.Empty,
            "Skill runner execution rejected.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = "rejected",
                ["reason"] = evt.Reason ?? string.Empty,
                ["cron_occurrence_key"] = evt.CronOccurrenceKey ?? string.Empty,
            });
}

public abstract class ScheduledAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
    where TEvent : class, IMessage<TEvent>, new()
{
    public abstract string EventTypeUrl { get; }

    public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(new TEvent().Descriptor))
            return [];

        var evt = eventPayload.Unpack<TEvent>();
        return [CommittedAuditRecordFactory.CreateSystemRecord(context, BuildSeed(context, evt))];
    }

    protected abstract CommittedAuditSeed BuildSeed(CommittedAuditTranslationContext context, TEvent evt);

    protected static CommittedAuditSeed ScheduledSeed(
        string operationName,
        string targetKind,
        string targetId,
        string scopeId,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        bool isDestructive = false,
        IReadOnlyDictionary<string, string>? annotations = null) =>
        new(
            operationName,
            targetKind,
            targetId,
            scopeId,
            sensitivityLevel,
            isDestructive,
            ResultSummary: resultSummary,
            Annotations: annotations);
}
