using Aevatar.ChatRouting.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.ChatRouting.Core;

public static class ChatRouteActionTargets
{
    private const string InvokeGAgentToolName = "aevatar_invoke_gagent";
    private const string ActorIdArgument = "actor_id";
    private const string VoiceModuleNameArgument = "voice_module_name";

    public static bool TryGetGAgentActorTarget(
        ChatRouteDecision? decision,
        out ChatRouteGAgentToolTarget target)
    {
        target = default;
        if (TryGetGAgentActorTarget(decision?.OriginalAction, out target))
            return true;

        return TryGetGAgentActorTarget(decision?.Action, out target);
    }

    public static bool TryGetGAgentActorTarget(
        ChatRouteAction? action,
        out ChatRouteGAgentToolTarget target)
    {
        target = default;
        if (action is null)
            return false;

        if (action.ForwardToModel?.ToolChoiceHint is not { } hint ||
            !string.Equals(hint.ToolName, InvokeGAgentToolName, StringComparison.Ordinal))
        {
            return false;
        }

        var actorId = ReadString(hint.PrefilledArguments, ActorIdArgument);
        if (string.IsNullOrWhiteSpace(actorId))
            return false;

        target = new ChatRouteGAgentToolTarget(
            actorId,
            ReadString(hint.PrefilledArguments, VoiceModuleNameArgument));
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

public readonly record struct ChatRouteGAgentToolTarget(
    string ActorId,
    string VoiceModuleName);
