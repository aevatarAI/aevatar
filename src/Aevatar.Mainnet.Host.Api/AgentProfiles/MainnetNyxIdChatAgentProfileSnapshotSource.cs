using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

public sealed class MainnetNyxIdChatAgentProfileSnapshotSource : INyxIdChatAgentProfileSnapshotSource
{
    private readonly AgentProfileSnapshot? _sealedSnapshot;

    public MainnetNyxIdChatAgentProfileSnapshotSource(IOptions<NyxIdChatAgentProfileOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value;
        _sealedSnapshot = configured.Enabled && configured.Profile is not null
            ? AgentProfileSnapshotCodec.Seal(configured.Profile)
            : null;
    }

    public AgentProfileSnapshot? GetSnapshotForNewConversation(string actorId) => _sealedSnapshot?.Clone();
}
