using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Surface classification shared by the NyxID platform tools. A tool declares
/// <see cref="HumanSessionOnly"/> when its broker endpoints only accept a human-session credential:
/// in a channel-relay turn the effective credential is a bot-class relay/API-key token that the
/// broker rejects on those surfaces (account, identity, keys, nodes, endpoints, providers, org,
/// admin, mfa, sessions, notifications, channel-bots, and service/approval management, plus the
/// account-aggregate status), so the tool can only fail if offered. The channel reply path filters
/// tools carrying this capability out of channel turns by capability, never by tool name.
/// </summary>
internal static class NyxIdToolSurfaces
{
    public static readonly IReadOnlyCollection<string> HumanSessionOnly =
        [AgentToolCapabilities.RequiresHumanSession];
}
