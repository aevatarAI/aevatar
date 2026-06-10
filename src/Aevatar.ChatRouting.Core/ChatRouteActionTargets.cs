using Aevatar.ChatRouting.Abstractions;

namespace Aevatar.ChatRouting.Core;

// Refactor (iter367/cluster-issue674): Old pattern: voice attach routing encoded
// actor_id and voice_module_name inside ForwardToModel.ToolChoiceHint.PrefilledArguments.
// New principle: typed ChatRouteVoiceAttachTarget is the only voice attach target
// contract; ForwardToModel without it remains ordinary model forwarding.
public static class ChatRouteActionTargets
{
    private const string DefaultToolSetName = "workspace.default";

    public static ChatRouteAction ForwardToVoiceAttachTarget(
        string actorId,
        string voiceModuleName = "",
        string toolSetName = DefaultToolSetName,
        Aevatar.Foundation.VoicePresence.Abstractions.VoiceSessionOverrides? sessionOverrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = toolSetName },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    VoiceAttachTarget = new ChatRouteVoiceAttachTarget
                    {
                        ActorId = actorId.Trim(),
                        VoiceModuleName = voiceModuleName.Trim(),
                        SessionOverrides = sessionOverrides?.Clone(),
                    },
                },
            },
        };
    }

    public static bool TryGetVoiceAttachTarget(
        ChatRouteAction? action,
        out ChatRouteVoiceAttachTarget target)
    {
        target = new ChatRouteVoiceAttachTarget();
        var candidate = action?.ForwardToModel?.ToolChoiceHint?.VoiceAttachTarget;
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.ActorId))
            return false;

        target = candidate;
        return true;
    }
}
