using Aevatar.Workflow.Abstractions;
using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Projection.Projectors;

internal static class WorkflowStepParameterProjectionSource
{
    public static MapField<string, string> From(StepRequestEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.Parameters;
    }
}
