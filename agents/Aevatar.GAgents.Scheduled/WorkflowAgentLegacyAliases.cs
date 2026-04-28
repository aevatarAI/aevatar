using Aevatar.Foundation.Abstractions.Compatibility;

namespace Aevatar.GAgents.Scheduled;

internal static class WorkflowAgentLegacyAliases
{
    private const string ProtoPrefix = "aevatar.gagents.channelruntime.";
    private const string ClrPrefix = "Aevatar.GAgents.ChannelRuntime.";

    internal const string StateProto = ProtoPrefix + "WorkflowAgentState";
    internal const string InitializeCommandProto = ProtoPrefix + "InitializeWorkflowAgentCommand";
    internal const string InitializedEventProto = ProtoPrefix + "WorkflowAgentInitializedEvent";
    internal const string TriggerCommandProto = ProtoPrefix + "TriggerWorkflowAgentExecutionCommand";
    internal const string NextRunScheduledEventProto = ProtoPrefix + "WorkflowAgentNextRunScheduledEvent";
    internal const string ExecutionDispatchedEventProto = ProtoPrefix + "WorkflowAgentExecutionDispatchedEvent";
    internal const string ExecutionFailedEventProto = ProtoPrefix + "WorkflowAgentExecutionFailedEvent";
    internal const string DisableCommandProto = ProtoPrefix + "DisableWorkflowAgentCommand";
    internal const string EnableCommandProto = ProtoPrefix + "EnableWorkflowAgentCommand";
    internal const string DisabledEventProto = ProtoPrefix + "WorkflowAgentDisabledEvent";
    internal const string EnabledEventProto = ProtoPrefix + "WorkflowAgentEnabledEvent";

    internal const string StateClr = ClrPrefix + "WorkflowAgentState";
}

[LegacyProtoFullName(WorkflowAgentLegacyAliases.StateProto)]
[LegacyClrTypeName(WorkflowAgentLegacyAliases.StateClr)]
public sealed partial class WorkflowAgentState;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.InitializeCommandProto)]
public sealed partial class InitializeWorkflowAgentCommand;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.InitializedEventProto)]
public sealed partial class WorkflowAgentInitializedEvent;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.TriggerCommandProto)]
public sealed partial class TriggerWorkflowAgentExecutionCommand;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.NextRunScheduledEventProto)]
public sealed partial class WorkflowAgentNextRunScheduledEvent;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.ExecutionDispatchedEventProto)]
public sealed partial class WorkflowAgentExecutionDispatchedEvent;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.ExecutionFailedEventProto)]
public sealed partial class WorkflowAgentExecutionFailedEvent;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.DisableCommandProto)]
public sealed partial class DisableWorkflowAgentCommand;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.EnableCommandProto)]
public sealed partial class EnableWorkflowAgentCommand;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.DisabledEventProto)]
public sealed partial class WorkflowAgentDisabledEvent;

[LegacyProtoFullName(WorkflowAgentLegacyAliases.EnabledEventProto)]
public sealed partial class WorkflowAgentEnabledEvent;
