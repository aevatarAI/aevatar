using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Resolves the complete text-delivery profile for a NyxID Relay platform.
/// These profiles do not define capabilities for native or direct adapters.
/// </summary>
public static class NyxRelayCapabilityProfiles
{
    public static ChannelCapabilities Resolve(string platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        return platform.Trim().ToLowerInvariant() switch
        {
            "telegram" => Profile(4096, false, ReplyMessageMultiplicity.Multiple),
            "lark" or "feishu" => Profile(30000, true, ReplyMessageMultiplicity.Multiple),
            // Aurinko enforces this limit in bytes, not as a provider character guarantee.
            "aurinko" => Profile(262144, false, ReplyMessageMultiplicity.Single),
            "device" => Profile(0, false, ReplyMessageMultiplicity.None),
            _ => Profile(2000, false, ReplyMessageMultiplicity.Multiple),
        };
    }

    private static ChannelCapabilities Profile(
        int maximumLength,
        bool supportsEdit,
        ReplyMessageMultiplicity multiplicity) => new()
        {
            MaxMessageLength = maximumLength,
            SupportsEdit = supportsEdit,
            ReplyMessageMultiplicity = multiplicity,
        };
}
