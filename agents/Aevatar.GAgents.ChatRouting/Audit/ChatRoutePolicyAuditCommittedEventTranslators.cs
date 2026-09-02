using System.Globalization;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.ChatRouting.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChatRouting.Audit;

/// <summary>
/// Committed-fact audit translator for the chat route policy aggregate. All
/// three write commands on <see cref="ChatRoutePolicyGAgent"/> (upsert policy,
/// upsert rule, remove rule) persist the same full-state
/// <see cref="ChatRoutePolicyUpdated"/> committed event, so a single translator
/// covers every state change.
///
/// The route policy is a per-scope config aggregate (actor id
/// <c>chat-route-policy:{scopeId}</c>) and carries no external subject in its
/// actor id — it is service/config-scoped, so the plain translator base is used
/// (no subject hashing). The seed records only safe references (policy id,
/// version, rule count, owner scope); it never records rule bodies, model names,
/// or tool prefill values, none of which are credentials but which are also not
/// needed to audit "the policy changed".
/// </summary>
public sealed class ChatRoutePolicyUpdatedAuditTranslator
    : ChatRoutePolicyAuditTranslatorBase<ChatRoutePolicyUpdated>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ChatRoutePolicyUpdated.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ChatRoutePolicyUpdated evt)
    {
        var state = evt.State;
        var policyId = string.IsNullOrEmpty(state?.PolicyId) ? context.OriginActorId : state.PolicyId;
        var scopeId = state?.OwnerScope?.RegistrationScopeId ?? string.Empty;
        var version = state?.Version ?? 0;
        var ruleCount = state?.Rules.Count ?? 0;

        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["policy_version"] = version.ToString(CultureInfo.InvariantCulture),
            ["rule_count"] = ruleCount.ToString(CultureInfo.InvariantCulture),
        };

        return new CommittedAuditSeed(
            "chat.route-policy.updated",
            "chat_route_policy",
            policyId,
            scopeId,
            // A route policy governs where inbound chat is dispatched — an
            // access-control-shaped config change — so it is recorded at
            // Confidential (access-control changes: Restricted or Confidential).
            AuditSensitivityLevel.Confidential,
            ResultSummary: $"Chat route policy updated to version {version} with {ruleCount} rule(s).",
            Annotations: annotations);
    }
}

/// <summary>
/// Plain (non-subject-bearing) committed-fact audit translator base for the chat
/// route policy module. Mirrors the service module's <c>AuditTranslatorBase</c>:
/// self-filters by exact type-url and produces a single system record per
/// matching committed event.
/// </summary>
public abstract class ChatRoutePolicyAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
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
}
