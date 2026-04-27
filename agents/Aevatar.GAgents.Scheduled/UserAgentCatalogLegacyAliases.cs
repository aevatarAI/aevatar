using Aevatar.Foundation.Abstractions.Compatibility;

namespace Aevatar.GAgents.Scheduled;

internal static class UserAgentCatalogLegacyAliases
{
    private const string ProtoPrefix = "aevatar.gagents.channelruntime." + "Agent" + "Registry";
    private const string ClrPrefix = "Aevatar.GAgents.ChannelRuntime." + "Agent" + "Registry";

    internal const string EntryProto = ProtoPrefix + "Entry";
    internal const string StateProto = ProtoPrefix + "State";
    internal const string UpsertCommandProto = ProtoPrefix + "UpsertCommand";
    internal const string TombstoneCommandProto = ProtoPrefix + "TombstoneCommand";
    internal const string ExecutionUpdateCommandProto = ProtoPrefix + "ExecutionUpdateCommand";
    internal const string CompactTombstonesCommandProto = ProtoPrefix + "CompactTombstonesCommand";
    internal const string UpsertedEventProto = ProtoPrefix + "UpsertedEvent";
    internal const string TombstonedEventProto = ProtoPrefix + "TombstonedEvent";
    internal const string ExecutionUpdatedEventProto = ProtoPrefix + "ExecutionUpdatedEvent";
    internal const string TombstonesCompactedEventProto = ProtoPrefix + "TombstonesCompactedEvent";
    internal const string DocumentProto = ProtoPrefix + "Document";
    internal const string NyxCredentialDocumentProto =
        "aevatar.gagents.channelruntime.UserAgentCatalogNyxCredentialDocument";

    internal const string StateClr = ClrPrefix + "State";
}

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.EntryProto)]
public sealed partial class UserAgentCatalogEntry;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.StateProto)]
[LegacyClrTypeName(UserAgentCatalogLegacyAliases.StateClr)]
public sealed partial class UserAgentCatalogState;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.UpsertCommandProto)]
public sealed partial class UserAgentCatalogUpsertCommand;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.TombstoneCommandProto)]
public sealed partial class UserAgentCatalogTombstoneCommand;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.ExecutionUpdateCommandProto)]
public sealed partial class UserAgentCatalogExecutionUpdateCommand;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.CompactTombstonesCommandProto)]
public sealed partial class UserAgentCatalogCompactTombstonesCommand;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.UpsertedEventProto)]
public sealed partial class UserAgentCatalogUpsertedEvent;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.TombstonedEventProto)]
public sealed partial class UserAgentCatalogTombstonedEvent;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.ExecutionUpdatedEventProto)]
public sealed partial class UserAgentCatalogExecutionUpdatedEvent;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.TombstonesCompactedEventProto)]
public sealed partial class UserAgentCatalogTombstonesCompactedEvent;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.DocumentProto)]
public sealed partial class UserAgentCatalogDocument;

[LegacyProtoFullName(UserAgentCatalogLegacyAliases.NyxCredentialDocumentProto)]
public sealed partial class UserAgentCatalogNyxCredentialDocument;
