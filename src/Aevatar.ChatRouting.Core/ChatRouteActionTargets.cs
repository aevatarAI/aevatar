using Aevatar.ChatRouting.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.ChatRouting.Core;

public static class ChatRouteActionTargets
{
    private const string InvokeGAgentToolName = "aevatar_invoke_gagent";
    private const string ActorIdArgument = "actor_id";

    public static bool TryGetGAgentActorTarget(
        ChatRouteDecision? decision,
        out ForwardToGAgent target)
    {
        target = new ForwardToGAgent();
        if (TryGetGAgentActorTarget(decision?.OriginalAction, out target))
            return true;

        return TryGetGAgentActorTarget(decision?.Action, out target);
    }

    public static bool TryGetGAgentActorTarget(
        ChatRouteAction? action,
        out ForwardToGAgent target)
    {
        target = new ForwardToGAgent();
        if (action is null)
            return false;

        if (action.ForwardToGagent is { } direct)
        {
            target = direct.Clone();
            return !string.IsNullOrWhiteSpace(target.ActorId);
        }

        if (action.ForwardToModel?.ToolChoiceHint is not { } hint ||
            !string.Equals(hint.ToolName, InvokeGAgentToolName, StringComparison.Ordinal))
        {
            return false;
        }

        var actorId = ReadString(hint.PrefilledArguments, ActorIdArgument);
        if (string.IsNullOrWhiteSpace(actorId))
            return false;

        target.ActorId = actorId;
        return true;
    }

    private static string ReadString(Struct? arguments, string key)
    {
        if (arguments?.Fields.TryGetValue(key, out var value) != true ||
            value.KindCase != Value.KindOneofCase.StringValue)
        {
            return string.Empty;
        }

        return value.StringValue?.Trim() ?? string.Empty;
    }
}
