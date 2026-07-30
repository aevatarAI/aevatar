using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity.Audit;

// Committed-fact audit translators for the cluster-singleton aevatar OAuth
// client (well-known actor id "aevatar-oauth-client"). These are host/cluster
// governance facts with no per-tenant scope and no external subject: the actor
// id is a fixed well-known constant, so it is safe to carry as the target id.
//
// Security boundary (docs/canon/audit-trail.md §4): HMAC key material and the
// state-token signing keys MUST NOT enter the audit artifact. The rotation
// translator records only the key ids (kid) and never the key bytes.

public sealed class AevatarOAuthClientProvisionedAuditTranslator
    : AevatarOAuthClientAuditTranslatorBase<AevatarOAuthClientProvisionedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AevatarOAuthClientProvisionedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AevatarOAuthClientProvisionedEvent evt) =>
        OAuthClientSeed(
            "identity.oauth-client.provisioned",
            context.OriginActorId,
            "Aevatar OAuth client configuration materialized by the identity actor.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = evt.ClientId ?? string.Empty,
                ["nyxid_authority"] = evt.NyxidAuthority ?? string.Empty,
            });
}

public sealed class AevatarOAuthClientHmacKeyRotatedAuditTranslator
    : AevatarOAuthClientAuditTranslatorBase<AevatarOAuthClientHmacKeyRotatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AevatarOAuthClientHmacKeyRotatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AevatarOAuthClientHmacKeyRotatedEvent evt) =>
        OAuthClientSeed(
            "identity.oauth-client.hmac-key.rotated",
            context.OriginActorId,
            "Aevatar OAuth client HMAC state-token signing key rotated.",
            AuditSensitivityLevel.Restricted,
            // Key ids only — the raw hmac_key / previous_hmac_key bytes are
            // structurally excluded from the audit artifact.
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hmac_kid"] = evt.HmacKid ?? string.Empty,
                ["previous_hmac_kid"] = evt.PreviousHmacKid ?? string.Empty,
            });
}

public sealed class AevatarOAuthClientBrokerCapabilityObservedAuditTranslator
    : AevatarOAuthClientAuditTranslatorBase<AevatarOAuthClientBrokerCapabilityObservedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AevatarOAuthClientBrokerCapabilityObservedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AevatarOAuthClientBrokerCapabilityObservedEvent evt) =>
        OAuthClientSeed(
            "identity.oauth-client.broker-capability.observed",
            context.OriginActorId,
            "Aevatar OAuth client broker capability observed enabled at NyxID.");
}

public sealed class AevatarOAuthClientDriftReconciledAuditTranslator
    : AevatarOAuthClientAuditTranslatorBase<AevatarOAuthClientDriftReconciledEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AevatarOAuthClientDriftReconciledEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AevatarOAuthClientDriftReconciledEvent evt) =>
        OAuthClientSeed(
            "identity.oauth-client.drift.reconciled",
            context.OriginActorId,
            "Aevatar OAuth client configuration drift reconciled via re-registration.",
            annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["drift_kind"] = evt.DriftKind ?? string.Empty,
                ["active_client_id"] = evt.ActiveClientId ?? string.Empty,
            });
}

public abstract class AevatarOAuthClientAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
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

    protected static CommittedAuditSeed OAuthClientSeed(
        string operationName,
        string targetId,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        IReadOnlyDictionary<string, string>? annotations = null) =>
        new(
            operationName,
            "aevatar_oauth_client",
            targetId,
            // Cluster-singleton host resource: no per-tenant scope.
            ScopeId: string.Empty,
            sensitivityLevel,
            IsDestructive: false,
            ResultSummary: resultSummary,
            Annotations: annotations);
}
