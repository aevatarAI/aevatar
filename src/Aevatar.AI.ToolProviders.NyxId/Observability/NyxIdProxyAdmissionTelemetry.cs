using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Observability;

internal static class NyxIdProxyAdmissionTelemetry
{
    public const string MeterName = "Aevatar.AI.ToolProviders.NyxId";
    public const string DecisionCounterName = "aevatar.nyxid.proxy.admission.decisions";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>(DecisionCounterName);

    public static void Record(
        NyxIdManagedWorkflowAdmissionMode mode,
        bool managed,
        bool proofPresent,
        AgentToolInvocationSurface invocationSurface,
        bool wouldBlock)
    {
        TagList tags = default;
        tags.Add("aevatar.nyxid.admission.mode", mode == NyxIdManagedWorkflowAdmissionMode.Enforce ? "enforce" : "shadow");
        tags.Add("aevatar.nyxid.admission.managed", managed);
        tags.Add("aevatar.nyxid.admission.proof_present", proofPresent);
        tags.Add("aevatar.nyxid.admission.invocation_surface", InvocationSurfaceName(invocationSurface));
        tags.Add("aevatar.nyxid.admission.risk", "unspecified");
        tags.Add("aevatar.nyxid.admission.would_approve", false);
        tags.Add("aevatar.nyxid.admission.would_block", wouldBlock);
        Decisions.Add(1, tags);
    }

    private static string InvocationSurfaceName(AgentToolInvocationSurface surface) =>
        surface switch
        {
            AgentToolInvocationSurface.HumanSession => "human_session",
            AgentToolInvocationSurface.WorkflowToolCall => "workflow_tool_call",
            AgentToolInvocationSurface.WorkflowLlmToolLoop => "workflow_llm_tool_loop",
            _ => "unspecified",
        };
}
