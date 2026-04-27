using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Runtime;

internal sealed class ChannelBotRegistrationTombstoneCompactionTarget : ITombstoneCompactionTarget
{
    public string ActorId => ChannelBotRegistrationGAgent.WellKnownId;
    public string ProjectionKind => ChannelBotRegistrationProjectionPort.ProjectionKind;
    public string TargetName => "channel bot registration";

    public IMessage CreateCommand(long safeStateVersion) =>
        new ChannelBotCompactTombstonesCommand { SafeStateVersion = safeStateVersion };
}
