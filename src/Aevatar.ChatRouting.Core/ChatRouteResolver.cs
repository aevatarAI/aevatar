using Aevatar.AI.ToolProviders.ToolSetRegistry;
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

        foreach (var rule in snapshot.Rules)
        {
            if (!Matches(rule.Match, input))
            {
                continue;
            }

            // A projected rule whose action was omitted is not actionable —
            // proto3 message fields can be null when not set, and the write
            // side validates only default_target on upsert, so a stored rule
            // can legitimately have no action. Skip it so resolution falls
            // through to the next rule (and, if no rule has an action, to
            // default_target) instead of NREing on action.Clone().
            if (!HasAction(rule.Action))
            {
                continue;
            }

            return NewDecision(rule.Action, matchedRuleId: rule.RuleId, usedFallback: false);
        }

        return NewDecision(snapshot.DefaultTarget, matchedRuleId: string.Empty, usedFallback: false);
    }

    private static bool HasAction(ChatRouteAction? action) =>
        action is not null && action.ActionCase != ChatRouteAction.ActionOneofCase.None;

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
               && MatchesEnum(match.ToolMode, ToolMode.Unspecified, input.ToolMode)
               && MatchesString(match.Model, input.Model);
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
            Action = ChatRouteActionTranslator.ToRuntimeDecisionAction(action),
            MatchedRuleId = matchedRuleId,
            UsedFallback = usedFallback,
            ResolvedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
}

internal static class ChatRouteActionTranslator
{
    internal const string InvokeGAgentToolName = "aevatar_invoke_gagent";
    internal const string InvokeTeamToolName = "aevatar_invoke_team";
    internal const string StartWorkflowToolName = "aevatar_start_workflow";

    private const string ActorIdArgument = "actor_id";
    private const string TeamIdArgument = "team_id";
    private const string EndpointIdArgument = "endpoint_id";
    private const string ScopeIdArgument = "scope_id";
    private const string WorkflowIdArgument = "workflow_id";

    public static ChatRouteAction ToRuntimeDecisionAction(ChatRouteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return action.ActionCase switch
        {
            ChatRouteAction.ActionOneofCase.ForwardToModel => action.Clone(),
            ChatRouteAction.ActionOneofCase.ForwardToGagent => ForwardToToolModel(
                InvokeGAgentToolName,
                [
                    new(ActorIdArgument, action.ForwardToGagent.ActorId, IncludeWhenEmpty: true),
                ]),
            ChatRouteAction.ActionOneofCase.ForwardToTeam => ForwardToToolModel(
                InvokeTeamToolName,
                [
                    new(TeamIdArgument, action.ForwardToTeam.TeamId, IncludeWhenEmpty: true),
                    new(EndpointIdArgument, action.ForwardToTeam.EndpointId, IncludeWhenEmpty: true),
                    new(ScopeIdArgument, action.ForwardToTeam.ScopeId, IncludeWhenEmpty: false),
                ]),
            ChatRouteAction.ActionOneofCase.ForwardToWorkflow => ForwardToToolModel(
                StartWorkflowToolName,
                [
                    new(WorkflowIdArgument, action.ForwardToWorkflow.WorkflowId, IncludeWhenEmpty: true),
                ]),
            ChatRouteAction.ActionOneofCase.Reject => action.Clone(),
            _ => action.Clone(),
        };
    }

    public static ChatRouteAction ToCanonicalPolicyAction(ChatRouteAction action) =>
        ToRuntimeDecisionAction(action);

    private static ChatRouteAction ForwardToToolModel(
        string toolName,
        IReadOnlyList<PrefilledArgument> prefilledArguments) =>
        new()
        {
            ForwardToModel = new ForwardToModel
            {
                ModelName = string.Empty,
                ToolSetRef = new ChatRouteToolSetRef { Name = ToolSetNames.WorkspaceDefault },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = toolName,
                    PrefilledArguments = BuildPrefilledArguments(prefilledArguments),
                },
            },
        };

    private static Struct BuildPrefilledArguments(IReadOnlyList<PrefilledArgument> arguments)
    {
        var result = new Struct();
        foreach (var argument in arguments)
        {
            if (!argument.IncludeWhenEmpty && string.IsNullOrEmpty(argument.Value))
            {
                continue;
            }

            result.Fields[argument.Name] = Value.ForString(argument.Value);
        }

        return result;
    }

    private sealed record PrefilledArgument(string Name, string Value, bool IncludeWhenEmpty);
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
