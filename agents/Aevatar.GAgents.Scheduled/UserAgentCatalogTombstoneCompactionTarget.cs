using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;

namespace Aevatar.GAgents.Scheduled;

internal sealed class UserAgentCatalogTombstoneCompactionTarget : ITombstoneCompactionTarget
{
    public string ActorId => UserAgentCatalogGAgent.WellKnownId;
    public string ProjectionKind => UserAgentCatalogProjectionPort.ProjectionKind;
    public string TargetName => "user agent catalog";

    public async Task EnsureActorAsync(IActorRuntime actorRuntime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actorRuntime);
        _ = await actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await actorRuntime.CreateAsync<UserAgentCatalogGAgent>(
                UserAgentCatalogGAgent.WellKnownId,
                ct);
    }

    public IMessage CreateCommand(long safeStateVersion) =>
        new UserAgentCatalogCompactTombstonesCommand { SafeStateVersion = safeStateVersion };
}
