using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection;

public static class WorkflowExecutionProjectionContextRunEventExtensions
{
    private const string RunEventSinkProperty = "aevatar.projection.run.sink";

    public static void SetRunEventSink(this WorkflowExecutionProjectionContext context, IWorkflowRunEventSink sink) =>
        context.SetProperty(RunEventSinkProperty, sink);

    public static IWorkflowRunEventSink? GetRunEventSink(this WorkflowExecutionProjectionContext context) =>
        context.TryGetProperty<IWorkflowRunEventSink>(RunEventSinkProperty, out var sink) ? sink : null;

    public static void DetachRunEventSink(this WorkflowExecutionProjectionContext context) =>
        context.RemoveProperty(RunEventSinkProperty);
}
