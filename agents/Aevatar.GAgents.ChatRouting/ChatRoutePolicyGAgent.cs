using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// Per-scope, long-lived config aggregate that owns the authoritative chat
/// route policy (issue #672 / ADR-0024). It is the single source of truth for
/// <see cref="ChatRoutePolicyState"/>; the <c>ChatRoutePolicyCurrentStateDocument</c>
/// readmodel is a query-side replica projected from its committed
/// <see cref="ChatRoutePolicyUpdated"/> events.
///
/// This actor only handles config commands — it never participates in turn
/// dispatch and never answers queries (queries read the readmodel, per
/// CLAUDE.md 读写分离).
///
/// Actor ID convention: <c>chat-route-policy:{scopeId}</c> (per-scope).
/// </summary>
[GAgent("chat.routing.policy")]
public sealed class ChatRoutePolicyGAgent : GAgentBase<ChatRoutePolicyState>, IProjectedActor
{
    public static string ProjectionKind => "chat-route-policy";

    /// <summary>
    /// Creates or replaces the policy for this scope. <c>default_target</c> is
    /// required so the resolver always has a fallback when no rule matches.
    /// Rules are persisted pre-ordered by priority so the resolver can evaluate
    /// them in stored order.
    /// </summary>
    [EventHandler]
    public async Task HandleUpsertAsync(UpsertChatRoutePolicyRequested command)
    {
        if (command.DefaultTarget is null ||
            command.DefaultTarget.ActionCase == ChatRouteAction.ActionOneofCase.None)
        {
            throw new InvalidOperationException(
                "UpsertChatRoutePolicyRequested.default_target is required: a chat route policy must " +
                "declare a default ChatRouteAction (e.g. ForwardToModel) so the resolver always has a " +
                "fallback when no rule matches. Set default_target to a non-empty ChatRouteAction.");
        }

        var nextState = new ChatRoutePolicyState
        {
            PolicyId = string.IsNullOrEmpty(State.PolicyId) ? Id : State.PolicyId,
            OwnerScope = command.OwnerScope?.Clone(),
            DefaultTarget = command.DefaultTarget.Clone(),
            Version = State.Version + 1,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        nextState.Rules.AddRange(OrderRulesByPriority(command.Rules));

        await PersistDomainEventAsync(new ChatRoutePolicyUpdated { State = nextState });
    }

    // Refactor (iter34/cluster-004-voice-bootstrap-application-port):
    //   Old pattern: Voice demo bootstrap read the route-policy readmodel and synthesized a full replacement policy outside the actor.
    //   New principle: Single-rule upserts are merged inside ChatRoutePolicyGAgent against authoritative actor state.
    /// <summary>
    /// Adds or replaces one rule while preserving all other authoritative actor
    /// state. If the policy is not initialized yet,
    /// <c>default_target_if_uninitialized</c> establishes the required fallback.
    /// </summary>
    [EventHandler]
    public async Task HandleUpsertRuleAsync(UpsertChatRouteRuleRequested command)
    {
        if (command.Rule is null || string.IsNullOrWhiteSpace(command.Rule.RuleId))
        {
            throw new InvalidOperationException(
                "UpsertChatRouteRuleRequested.rule.rule_id is required.");
        }

        var hasExistingPolicy = IsInitialized();
        if (!hasExistingPolicy &&
            (command.DefaultTargetIfUninitialized is null ||
             command.DefaultTargetIfUninitialized.ActionCase == ChatRouteAction.ActionOneofCase.None))
        {
            throw new InvalidOperationException(
                "UpsertChatRouteRuleRequested.default_target_if_uninitialized is required when the policy is not initialized.");
        }

        var nextState = new ChatRoutePolicyState
        {
            PolicyId = string.IsNullOrEmpty(State.PolicyId) ? Id : State.PolicyId,
            OwnerScope = hasExistingPolicy
                ? State.OwnerScope?.Clone()
                : command.OwnerScope?.Clone(),
            DefaultTarget = hasExistingPolicy
                ? State.DefaultTarget.Clone()
                : command.DefaultTargetIfUninitialized.Clone(),
            Version = State.Version + 1,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        nextState.Rules.AddRange(OrderRulesByPriority(State.Rules
            .Where(rule => !string.Equals(rule.RuleId, command.Rule.RuleId, StringComparison.Ordinal))
            .Append(command.Rule)));

        await PersistDomainEventAsync(new ChatRoutePolicyUpdated { State = nextState });
    }

    /// <summary>
    /// Removes a single rule by id. Rejects an empty id, an uninitialized
    /// policy, and an unknown rule id with an actionable error rather than
    /// silently no-op'ing.
    /// </summary>
    [EventHandler]
    public async Task HandleRemoveRuleAsync(RemoveChatRouteRuleRequested command)
    {
        if (string.IsNullOrWhiteSpace(command.RuleId))
        {
            throw new InvalidOperationException(
                "RemoveChatRouteRuleRequested.rule_id is required.");
        }

        if (!IsInitialized())
        {
            throw new InvalidOperationException(
                $"chat route policy '{Id}' is not initialized; upsert a policy before removing rules.");
        }

        var retained = State.Rules
            .Where(rule => !string.Equals(rule.RuleId, command.RuleId, StringComparison.Ordinal))
            .Select(rule => rule.Clone())
            .ToList();
        if (retained.Count == State.Rules.Count)
        {
            throw new InvalidOperationException(
                $"chat route rule '{command.RuleId}' not found in policy '{State.PolicyId}'.");
        }

        var nextState = new ChatRoutePolicyState
        {
            PolicyId = State.PolicyId,
            OwnerScope = State.OwnerScope?.Clone(),
            DefaultTarget = State.DefaultTarget.Clone(),
            Version = State.Version + 1,
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        nextState.Rules.AddRange(retained);

        await PersistDomainEventAsync(new ChatRoutePolicyUpdated { State = nextState });
    }

    protected override ChatRoutePolicyState TransitionState(
        ChatRoutePolicyState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChatRoutePolicyUpdated>(ApplyPolicyUpdated)
            .OrCurrent();

    // ChatRoutePolicyUpdated carries the full next state, so applying it is a
    // straight replacement — the actor never derives state field-by-field.
    private static ChatRoutePolicyState ApplyPolicyUpdated(
        ChatRoutePolicyState current, ChatRoutePolicyUpdated evt)
        => evt.State.Clone();

    // A policy "exists" once a valid default_target has been upserted.
    private bool IsInitialized() =>
        State.DefaultTarget is not null &&
        State.DefaultTarget.ActionCase != ChatRouteAction.ActionOneofCase.None;

    // Higher priority first; ties broken by rule_id lexical order — matches the
    // ChatRouteRule proto contract. Persisting rules pre-ordered means the
    // resolver evaluates them in stored order without re-sorting.
    private static IEnumerable<ChatRouteRule> OrderRulesByPriority(
        IEnumerable<ChatRouteRule> rules) =>
        rules
            .Where(rule => rule is not null)
            .Select(rule => rule.Clone())
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.RuleId, StringComparer.Ordinal);
}
