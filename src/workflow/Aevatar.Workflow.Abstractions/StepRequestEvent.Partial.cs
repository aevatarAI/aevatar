using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Abstractions;

public sealed partial class StepRequestEvent
{
    public MapField<string, string> Parameters => (StepParameters ??= new WorkflowStepParameters()).Parameters;
}
