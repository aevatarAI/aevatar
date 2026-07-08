using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Platform.Lark;

public sealed class LarkRelayProxyResponseClassifier : IChannelRelayProxyResponseClassifier
{
    public ChannelRelayProxyResponseClassification Classify(string? response)
    {
        if (!LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
            return ChannelRelayProxyResponseClassification.Success();

        var kind = larkCode == LarkBotErrorCodes.NoPermissionToReact
            ? ChannelRelayProxyResponseKind.PermissionDenied
            : ChannelRelayProxyResponseKind.ProviderError;
        return ChannelRelayProxyResponseClassification.Error(
            kind,
            detail,
            larkCode?.ToString());
    }
}
