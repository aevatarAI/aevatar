using Aevatar.Foundation.Abstractions.Compatibility;

namespace Aevatar.GAgents.Channel.Runtime;

internal static class ChannelBotRegistrationLegacyAliases
{
    private const string ProtoPrefix = "aevatar.gagents.channelruntime.";
    private const string ClrPrefix = "Aevatar.GAgents.ChannelRuntime.";

    internal const string EntryProto = ProtoPrefix + "ChannelBotRegistrationEntry";
    internal const string StoreStateProto = ProtoPrefix + "ChannelBotRegistrationStoreState";
    internal const string DocumentProto = ProtoPrefix + "ChannelBotRegistrationDocument";
    internal const string RegisteredEventProto = ProtoPrefix + "ChannelBotRegisteredEvent";
    internal const string UnregisteredEventProto = ProtoPrefix + "ChannelBotUnregisteredEvent";
    internal const string RegisterCommandProto = ProtoPrefix + "ChannelBotRegisterCommand";
    internal const string UnregisterCommandProto = ProtoPrefix + "ChannelBotUnregisterCommand";
    internal const string RebuildProjectionCommandProto = ProtoPrefix + "ChannelBotRebuildProjectionCommand";
    internal const string CompactTombstonesCommandProto = ProtoPrefix + "ChannelBotCompactTombstonesCommand";
    internal const string ProjectionRebuildRequestedEventProto = ProtoPrefix + "ChannelBotProjectionRebuildRequestedEvent";
    internal const string TombstonesCompactedEventProto = ProtoPrefix + "ChannelBotTombstonesCompactedEvent";
    internal const string ChannelInboundEventProto = ProtoPrefix + "ChannelInboundEvent";

    internal const string StoreStateClr = ClrPrefix + "ChannelBotRegistrationStoreState";
}

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.EntryProto)]
public sealed partial class ChannelBotRegistrationEntry;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.StoreStateProto)]
[LegacyClrTypeName(ChannelBotRegistrationLegacyAliases.StoreStateClr)]
public sealed partial class ChannelBotRegistrationStoreState;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.DocumentProto)]
public sealed partial class ChannelBotRegistrationDocument;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.RegisteredEventProto)]
public sealed partial class ChannelBotRegisteredEvent;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.UnregisteredEventProto)]
public sealed partial class ChannelBotUnregisteredEvent;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.RegisterCommandProto)]
public sealed partial class ChannelBotRegisterCommand;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.UnregisterCommandProto)]
public sealed partial class ChannelBotUnregisterCommand;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.RebuildProjectionCommandProto)]
public sealed partial class ChannelBotRebuildProjectionCommand;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.CompactTombstonesCommandProto)]
public sealed partial class ChannelBotCompactTombstonesCommand;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.ProjectionRebuildRequestedEventProto)]
public sealed partial class ChannelBotProjectionRebuildRequestedEvent;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.TombstonesCompactedEventProto)]
public sealed partial class ChannelBotTombstonesCompactedEvent;

[LegacyProtoFullName(ChannelBotRegistrationLegacyAliases.ChannelInboundEventProto)]
public sealed partial class ChannelInboundEvent;
