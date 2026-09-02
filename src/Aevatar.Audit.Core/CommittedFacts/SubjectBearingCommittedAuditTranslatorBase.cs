using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Abstractions.Identity;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Core.CommittedFacts;

/// <summary>
/// Base class for committed-fact audit translators whose owning actor id embeds
/// a raw external subject (e.g. an external-identity binding keyed by
/// platform/tenant/external_user_id, or a per-sender credential binding). The
/// origin actor id is HMAC-hashed via <see cref="IAuditActorIdentityHasher"/> so
/// no raw subject enters the artifact. Seeds produced by subclasses are marked
/// <see cref="CommittedAuditSeed.SubjectBearing"/> automatically.
/// </summary>
public abstract class SubjectBearingCommittedAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
    where TEvent : class, IMessage<TEvent>, new()
{
    private readonly IAuditActorIdentityHasher? _actorIdentityHasher;

    // The hasher is host-owned (its HMAC secret is host configuration, registered
    // by AddAuditTrailCore). It is injected optionally so a host that has not
    // configured the audit identity hasher degrades gracefully: the translator
    // skips the record rather than crashing the shared projection pipeline or
    // leaking a raw subject. This mirrors the fail-open posture of the boundary
    // and tool audit planes.
    protected SubjectBearingCommittedAuditTranslatorBase(IAuditActorIdentityHasher? actorIdentityHasher = null)
    {
        _actorIdentityHasher = actorIdentityHasher;
    }

    public abstract string EventTypeUrl { get; }

    public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(new TEvent().Descriptor))
            return [];

        // No hasher configured -> cannot sanitize the subject -> skip rather than
        // leak. Fail-open for availability, never fail-open for confidentiality.
        if (_actorIdentityHasher is null)
            return [];

        var evt = eventPayload.Unpack<TEvent>();
        var seed = BuildSeed(context, evt) with { SubjectBearing = true };
        return [CommittedAuditRecordFactory.CreateSystemRecord(context, seed, _actorIdentityHasher)];
    }

    protected abstract CommittedAuditSeed BuildSeed(CommittedAuditTranslationContext context, TEvent evt);
}
