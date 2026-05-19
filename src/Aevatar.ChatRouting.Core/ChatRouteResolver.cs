using Aevatar.ChatRouting.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.ChatRouting.Core;

/// <summary>
/// Stateless boundary resolver for ingress chat routing decisions.
/// </summary>
public sealed class ChatRouteResolver
{
    private readonly IChatRouteFallbackProvider _fallbackProvider;

    public ChatRouteResolver(IChatRouteFallbackProvider fallbackProvider)
    {
        _fallbackProvider = fallbackProvider ?? throw new ArgumentNullException(nameof(fallbackProvider));
    }

    // Implement (issue #693):
    //   Behavior: resolve snapshot rules by priority, then default_target, then env/options fallback.
    //   Why this shape: ingress entries need a deterministic library decision without actor hops or IO.
    public ChatRouteDecision Resolve(ChatRoutePolicySnapshot? snapshot, ChatRouteInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (snapshot is null)
        {
            return _fallbackProvider.GetFallbackDecision();
        }

        foreach (var rule in snapshot.Rules
                     .OrderByDescending(static rule => rule.Priority)
                     .ThenBy(static rule => rule.RuleId, StringComparer.Ordinal))
        {
            if (!Matches(rule.Match, input))
            {
                continue;
            }

            return NewDecision(rule.Action, matchedRuleId: rule.RuleId, usedFallback: false);
        }

        return NewDecision(snapshot.DefaultTarget, matchedRuleId: string.Empty, usedFallback: false);
    }

    private static bool Matches(ChatRouteMatch? match, ChatRouteInput input)
    {
        if (match is null)
        {
            return true;
        }

        return MatchesEnum(match.SourceKind, ChatSourceKind.Unspecified, input.SourceKind)
               && MatchesString(match.Channel, input.Channel)
               && MatchesString(match.CommandName, input.CommandName)
               && MatchesString(match.ContentHint, input.ContentHint)
               && MatchesEnum(match.ToolMode, ToolMode.Unspecified, input.ToolMode);
    }

    private static bool MatchesString(string? expected, string actual) =>
        string.IsNullOrEmpty(expected) || string.Equals(expected, actual, StringComparison.Ordinal);

    private static bool MatchesEnum<TEnum>(TEnum expected, TEnum unspecified, TEnum actual)
        where TEnum : struct, System.Enum =>
        EqualityComparer<TEnum>.Default.Equals(expected, unspecified) ||
        EqualityComparer<TEnum>.Default.Equals(expected, actual);

    internal static ChatRouteDecision NewDecision(
        ChatRouteAction action,
        string matchedRuleId,
        bool usedFallback) =>
        new()
        {
            Action = action.Clone(),
            MatchedRuleId = matchedRuleId,
            UsedFallback = usedFallback,
            ResolvedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
}

/// <summary>
/// Immutable resolver input copied from the committed policy readmodel.
/// </summary>
public sealed class ChatRoutePolicySnapshot
{
    public ChatRoutePolicySnapshot(ChatRouteAction defaultTarget, IEnumerable<ChatRouteRule>? rules)
    {
        ArgumentNullException.ThrowIfNull(defaultTarget);
        if (defaultTarget.ActionCase == ChatRouteAction.ActionOneofCase.None)
        {
            throw new ArgumentException(
                "Chat route policy snapshot requires a non-empty default target.",
                nameof(defaultTarget));
        }

        DefaultTarget = defaultTarget.Clone();
        Rules = (rules ?? [])
            .Where(static rule => rule is not null)
            .Select(static rule => rule.Clone())
            .ToArray();
    }

    public ChatRouteAction DefaultTarget { get; }

    public IReadOnlyList<ChatRouteRule> Rules { get; }
}
