using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled.Audit;

// Committed-fact audit translators for scheduled user-agent catalog lifecycle events.
// The user-agent catalog actor carries opaque, non-subject ids; credentials are excluded
// from audit artifacts and only safe ids, scopes, kinds, and labels are recorded.

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
