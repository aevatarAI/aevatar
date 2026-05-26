using Aevatar.Foundation.Abstractions.Compatibility;

namespace Aevatar.GAgents.Device;

internal static class DeviceRegistrationLegacyAliases
{
    private const string ProtoPrefix = "aevatar.gagents.channelruntime.";
    private const string ClrPrefix = "Aevatar.GAgents.ChannelRuntime.";

    internal const string EntryProto = ProtoPrefix + "DeviceRegistrationEntry";
    internal const string StateProto = ProtoPrefix + "DeviceRegistrationState";
    internal const string DocumentProto = ProtoPrefix + "DeviceRegistrationDocument";
    internal const string RegisteredEventProto = ProtoPrefix + "DeviceRegisteredEvent";
    internal const string UnregisteredEventProto = ProtoPrefix + "DeviceUnregisteredEvent";
    internal const string RegisterCommandProto = ProtoPrefix + "DeviceRegisterCommand";
    internal const string UnregisterCommandProto = ProtoPrefix + "DeviceUnregisterCommand";
    internal const string CompactTombstonesCommandProto = ProtoPrefix + "DeviceCompactTombstonesCommand";
    internal const string TombstonesCompactedEventProto = ProtoPrefix + "DeviceTombstonesCompactedEvent";

    internal const string StateClr = ClrPrefix + "DeviceRegistrationState";
}

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.EntryProto)]
public sealed partial class DeviceRegistrationEntry;

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.StateProto)]
[LegacyClrTypeName(DeviceRegistrationLegacyAliases.StateClr)]
public sealed partial class DeviceRegistrationState;

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.DocumentProto)]
public sealed partial class DeviceRegistrationDocument;

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.RegisteredEventProto)]
public sealed partial class DeviceRegisteredEvent;

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.UnregisteredEventProto)]
public sealed partial class DeviceUnregisteredEvent;

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.RegisterCommandProto)]
public sealed partial class DeviceRegisterCommand;

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.UnregisterCommandProto)]
public sealed partial class DeviceUnregisterCommand;

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.CompactTombstonesCommandProto)]
public sealed partial class DeviceCompactTombstonesCommand;

[LegacyProtoFullName(DeviceRegistrationLegacyAliases.TombstonesCompactedEventProto)]
public sealed partial class DeviceTombstonesCompactedEvent;
