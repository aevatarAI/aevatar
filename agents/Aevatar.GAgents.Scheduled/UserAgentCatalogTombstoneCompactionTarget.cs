using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;

namespace Aevatar.GAgents.Scheduled;

internal sealed class UserAgentCatalogTombstoneCompactionTarget : ITombstoneCompactionTarget
{
    public string ActorId => UserAgentCatalogGAgent.WellKnownId;
    public string ProjectionKind => UserAgentCatalogProjectionPort.ProjectionKind;
    public string TargetName => "user agent catalog";

    public IMessage CreateCommand(long safeStateVersion) =>
        new UserAgentCatalogCompactTombstonesCommand { SafeStateVersion = safeStateVersion };
}
