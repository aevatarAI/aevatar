using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Runtime;

internal sealed class ChannelBotRegistrationTombstoneCompactionTarget : ITombstoneCompactionTarget
{
    public string ActorId => ChannelBotRegistrationGAgent.WellKnownId;
    public string ProjectionKind => ChannelBotRegistrationProjectionPort.ProjectionKind;
    public string TargetName => "channel bot registration";

    public async Task EnsureActorAsync(IActorRuntime actorRuntime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actorRuntime);
        _ = await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                ChannelBotRegistrationGAgent.WellKnownId,
                ct);
    }

    public IMessage CreateCommand(long safeStateVersion) =>
        new ChannelBotCompactTombstonesCommand { SafeStateVersion = safeStateVersion };
}
