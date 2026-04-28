using Aevatar.Foundation.Abstractions.Compatibility;

namespace Aevatar.GAgents.Scheduled;

internal static class SkillRunnerLegacyAliases
{
    private const string ProtoPrefix = "aevatar.gagents.channelruntime.";
    private const string ClrPrefix = "Aevatar.GAgents.ChannelRuntime.";

    internal const string OutboundConfigProto = ProtoPrefix + "SkillRunnerOutboundConfig";
    internal const string StateProto = ProtoPrefix + "SkillRunnerState";
    internal const string InitializeCommandProto = ProtoPrefix + "InitializeSkillRunnerCommand";
    internal const string InitializedEventProto = ProtoPrefix + "SkillRunnerInitializedEvent";
    internal const string TriggerCommandProto = ProtoPrefix + "TriggerSkillRunnerExecutionCommand";
    internal const string NextRunScheduledEventProto = ProtoPrefix + "SkillRunnerNextRunScheduledEvent";
    internal const string CompletedEventProto = ProtoPrefix + "SkillRunnerExecutionCompletedEvent";
    internal const string FailedEventProto = ProtoPrefix + "SkillRunnerExecutionFailedEvent";
    internal const string DisableCommandProto = ProtoPrefix + "DisableSkillRunnerCommand";
    internal const string EnableCommandProto = ProtoPrefix + "EnableSkillRunnerCommand";
    internal const string DisabledEventProto = ProtoPrefix + "SkillRunnerDisabledEvent";
    internal const string EnabledEventProto = ProtoPrefix + "SkillRunnerEnabledEvent";

    internal const string StateClr = ClrPrefix + "SkillRunnerState";
}

[LegacyProtoFullName(SkillRunnerLegacyAliases.OutboundConfigProto)]
public sealed partial class SkillRunnerOutboundConfig;

[LegacyProtoFullName(SkillRunnerLegacyAliases.StateProto)]
[LegacyClrTypeName(SkillRunnerLegacyAliases.StateClr)]
public sealed partial class SkillRunnerState;

[LegacyProtoFullName(SkillRunnerLegacyAliases.InitializeCommandProto)]
public sealed partial class InitializeSkillRunnerCommand;

[LegacyProtoFullName(SkillRunnerLegacyAliases.InitializedEventProto)]
public sealed partial class SkillRunnerInitializedEvent;

[LegacyProtoFullName(SkillRunnerLegacyAliases.TriggerCommandProto)]
public sealed partial class TriggerSkillRunnerExecutionCommand;

[LegacyProtoFullName(SkillRunnerLegacyAliases.NextRunScheduledEventProto)]
public sealed partial class SkillRunnerNextRunScheduledEvent;

[LegacyProtoFullName(SkillRunnerLegacyAliases.CompletedEventProto)]
public sealed partial class SkillRunnerExecutionCompletedEvent;

[LegacyProtoFullName(SkillRunnerLegacyAliases.FailedEventProto)]
public sealed partial class SkillRunnerExecutionFailedEvent;

[LegacyProtoFullName(SkillRunnerLegacyAliases.DisableCommandProto)]
public sealed partial class DisableSkillRunnerCommand;

[LegacyProtoFullName(SkillRunnerLegacyAliases.EnableCommandProto)]
public sealed partial class EnableSkillRunnerCommand;

[LegacyProtoFullName(SkillRunnerLegacyAliases.DisabledEventProto)]
public sealed partial class SkillRunnerDisabledEvent;

[LegacyProtoFullName(SkillRunnerLegacyAliases.EnabledEventProto)]
public sealed partial class SkillRunnerEnabledEvent;
