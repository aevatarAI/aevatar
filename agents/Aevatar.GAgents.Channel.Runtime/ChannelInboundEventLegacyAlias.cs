using Aevatar.Foundation.Abstractions.Compatibility;

namespace Aevatar.GAgents.Channel.Runtime;

// ChannelInboundEvent was historically emitted under the legacy proto package
// `aevatar.gagents.channelruntime.ChannelInboundEvent`. Preserve the legacy full-name alias so
// already-persisted relay inbound facts continue to deserialize after the message was relocated
// from channel_bot_registration.proto into channel_inbound.proto.
[LegacyProtoFullName(ChannelInboundEventLegacyAlias.ChannelInboundEventProto)]
public sealed partial class ChannelInboundEvent;

internal static class ChannelInboundEventLegacyAlias
{
    internal const string ChannelInboundEventProto = "aevatar.gagents.channelruntime.ChannelInboundEvent";
}
