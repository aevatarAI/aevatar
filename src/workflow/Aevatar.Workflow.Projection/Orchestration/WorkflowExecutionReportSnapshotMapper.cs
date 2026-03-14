using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

internal static class WorkflowExecutionReportSnapshotMapper
{
    public static Any Pack(WorkflowExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Any.Pack(report);
    }

    public static bool TryUnpack(Any? payload, out WorkflowExecutionReport? report)
    {
        report = null;
        if (payload == null || !payload.Is(WorkflowExecutionReport.Descriptor))
            return false;

        try
        {
            report = payload.Unpack<WorkflowExecutionReport>();
            return true;
        }
        catch (InvalidProtocolBufferException)
        {
            report = null;
            return false;
        }
    }
}
