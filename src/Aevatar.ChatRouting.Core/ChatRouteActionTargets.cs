using Aevatar.ChatRouting.Abstractions;

namespace Aevatar.ChatRouting.Core;

public static class ChatRouteActionTargets
{
    private const string DefaultToolSetName = "workspace.default";

    public static ChatRouteAction ForwardToVoiceAttachTarget(
        string actorId,
        string voiceModuleName = "",
        string toolSetName = DefaultToolSetName)
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
