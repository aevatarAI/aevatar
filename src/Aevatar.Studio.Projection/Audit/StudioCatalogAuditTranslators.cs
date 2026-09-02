using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;

namespace Aevatar.Studio.Projection.Audit;

// ─── Agent registry (admission) ───
//
// The registry actor admits agent actors into scope-keyed groups. actor_id is
// a scope/agent-addressed actor id, never a raw external subject, so a plain
// (non-subject-bearing) translator applies.

public sealed class ActorRegisteredAuditTranslator : StudioAuditTranslatorBase<ActorRegisteredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ActorRegisteredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ActorRegisteredEvent evt) =>
        StudioSeed(
            "registry.actor.registered",
            "registry_actor",
            evt.ActorId,
            "",
            "Agent actor registered.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agent_kind"] = evt.AgentKind,
            });
}

public sealed class ActorUnregisteredAuditTranslator : StudioAuditTranslatorBase<ActorUnregisteredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ActorUnregisteredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ActorUnregisteredEvent evt) =>
        StudioSeed(
            "registry.actor.unregistered",
            "registry_actor",
            evt.ActorId,
            "",
            "Agent actor unregistered.",
            AuditSensitivityLevel.Restricted,
            true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agent_kind"] = evt.AgentKind,
            });
}

// ─── Connector catalog ───
//
// Connector configs may carry auth/credential fields (client_secret, header
// values, secret_ref). Only connector identity (name/type) is recorded — never
// any auth value.

public sealed class ConnectorCatalogSavedAuditTranslator
    : StudioAuditTranslatorBase<ConnectorCatalogSavedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ConnectorCatalogSavedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ConnectorCatalogSavedEvent evt) =>
        StudioSeed(
            "connector.catalog.saved",
            "connector_catalog",
            context.OriginActorId,
            "",
            "Connector catalog saved.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["connector_count"] = evt.Connectors.Count.ToString(),
                ["connector_names"] = string.Join(",", evt.Connectors.Select(static connector => connector.Name)),
                ["connector_types"] = string.Join(",", evt.Connectors.Select(static connector => connector.Type)),
            });
}

public sealed class ConnectorDraftSavedAuditTranslator
    : StudioAuditTranslatorBase<ConnectorDraftSavedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ConnectorDraftSavedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ConnectorDraftSavedEvent evt) =>
        StudioSeed(
            "connector.draft.saved",
            "connector_catalog",
            context.OriginActorId,
            "",
            "Connector draft saved.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["connector_name"] = evt.Draft?.Name ?? string.Empty,
                ["connector_type"] = evt.Draft?.Type ?? string.Empty,
            });
}

public sealed class ConnectorDraftDeletedAuditTranslator
    : StudioAuditTranslatorBase<ConnectorDraftDeletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ConnectorDraftDeletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ConnectorDraftDeletedEvent evt) =>
        StudioSeed(
            "connector.draft.deleted",
            "connector_catalog",
            context.OriginActorId,
            "",
            "Connector draft deleted.",
            AuditSensitivityLevel.Restricted,
            true);
}

// ─── Role catalog ───
//
// Role definitions carry a system prompt and model config. Only role
// id/name/model are recorded; prompt bodies and any secrets are excluded.

public sealed class RoleCatalogSavedAuditTranslator : StudioAuditTranslatorBase<RoleCatalogSavedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(RoleCatalogSavedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        RoleCatalogSavedEvent evt) =>
        StudioSeed(
            "role.catalog.saved",
            "role_catalog",
            context.OriginActorId,
            "",
            "Role catalog saved.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role_count"] = evt.Roles.Count.ToString(),
                ["role_ids"] = string.Join(",", evt.Roles.Select(static role => role.Id)),
                ["role_names"] = string.Join(",", evt.Roles.Select(static role => role.Name)),
                ["role_models"] = string.Join(",", evt.Roles.Select(static role => role.Model)),
            });
}

public sealed class RoleDraftSavedAuditTranslator : StudioAuditTranslatorBase<RoleDraftSavedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(RoleDraftSavedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        RoleDraftSavedEvent evt) =>
        StudioSeed(
            "role.draft.saved",
            "role_catalog",
            context.OriginActorId,
            "",
            "Role draft saved.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role_id"] = evt.Draft?.Id ?? string.Empty,
                ["role_name"] = evt.Draft?.Name ?? string.Empty,
                ["role_model"] = evt.Draft?.Model ?? string.Empty,
            });
}

public sealed class RoleDraftDeletedAuditTranslator : StudioAuditTranslatorBase<RoleDraftDeletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(RoleDraftDeletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        RoleDraftDeletedEvent evt) =>
        StudioSeed(
            "role.draft.deleted",
            "role_catalog",
            context.OriginActorId,
            "",
            "Role draft deleted.",
            AuditSensitivityLevel.Restricted,
            true);
}
