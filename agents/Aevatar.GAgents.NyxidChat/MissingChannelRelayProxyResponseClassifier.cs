using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

internal sealed class MissingChannelRelayProxyResponseClassifier : IChannelRelayProxyResponseClassifier
{
    public ChannelRelayProxyResponseClassification Classify(string? response) =>
        ChannelRelayProxyResponseClassification.Success();
}
