using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Core.CommittedFacts;

namespace Aevatar.GAgents.Channel.Identity.Audit;

// Committed-fact audit translators for external-identity bindings.
//
// The owning actor (ExternalIdentityBindingGAgent) keys itself as
// "external-identity-binding:{platform}:{tenant}:{external_user_id}", so its
// actor id embeds the RAW external subject. These translators derive from
// SubjectBearingCommittedAuditTranslatorBase, which HMAC-hashes the origin actor
// id via IAuditActorIdentityHasher before it is stamped, so no raw subject
// enters the artifact (docs/canon/audit-trail.md §4).
//
// The NyxID binding_id (an opaque sender binding pointer, canon §4 forbidden) is
// never recorded. Only the platform and the audit-safe revoke reason are kept.

public sealed class ExternalIdentityBoundAuditTranslator
    : SubjectBearingCommittedAuditTranslatorBase<ExternalIdentityBoundEvent>
{
    public ExternalIdentityBoundAuditTranslator(IAuditActorIdentityHasher? actorIdentityHasher = null)
        : base(actorIdentityHasher)
    {
    }

    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ExternalIdentityBoundEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ExternalIdentityBoundEvent evt) =>
        new(
            "identity.external-binding.bound",
            "external_identity_binding",
            evt.ExternalSubject?.Platform ?? string.Empty,
            ScopeId: string.Empty,
            ResultSummary: "External identity binding committed.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["platform"] = evt.ExternalSubject?.Platform ?? string.Empty,
            });
}

public sealed class ExternalIdentityBindingReplacedAuditTranslator
    : SubjectBearingCommittedAuditTranslatorBase<ExternalIdentityBindingReplacedEvent>
{
    public ExternalIdentityBindingReplacedAuditTranslator(IAuditActorIdentityHasher? actorIdentityHasher = null)
        : base(actorIdentityHasher)
    {
    }

    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ExternalIdentityBindingReplacedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ExternalIdentityBindingReplacedEvent evt) =>
        new(
            "identity.external-binding.replaced",
            "external_identity_binding",
            evt.ExternalSubject?.Platform ?? string.Empty,
            ScopeId: string.Empty,
            SensitivityLevel: AuditSensitivityLevel.Restricted,
            ResultSummary: "External identity binding replaced.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["platform"] = evt.ExternalSubject?.Platform ?? string.Empty,
                ["reason"] = evt.Reason ?? string.Empty,
            });
}

public sealed class ExternalIdentityBindingRevokedAuditTranslator
    : SubjectBearingCommittedAuditTranslatorBase<ExternalIdentityBindingRevokedEvent>
{
    public ExternalIdentityBindingRevokedAuditTranslator(IAuditActorIdentityHasher? actorIdentityHasher = null)
        : base(actorIdentityHasher)
    {
    }

    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ExternalIdentityBindingRevokedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ExternalIdentityBindingRevokedEvent evt) =>
        new(
            "identity.external-binding.revoked",
            "external_identity_binding",
            evt.ExternalSubject?.Platform ?? string.Empty,
            ScopeId: string.Empty,
            SensitivityLevel: AuditSensitivityLevel.Restricted,
            IsDestructive: true,
            ResultSummary: "External identity binding revoked.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["platform"] = evt.ExternalSubject?.Platform ?? string.Empty,
                ["reason"] = evt.Reason ?? string.Empty,
            });
}
