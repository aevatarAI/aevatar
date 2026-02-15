using Aevatar.CQRS.Projection.WorkflowExecution;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Presentation.AGUI.Adapter.WorkflowExecution;

public static class WorkflowExecutionProjectionContextAGUIExtensions
{
    private const string AGUIEventSinkProperty = "aevatar.projection.agui.sink";

    public static void SetAGUIEventSink(this WorkflowExecutionProjectionContext context, IAGUIEventSink sink) =>
        context.SetProperty(AGUIEventSinkProperty, sink);

    public static IAGUIEventSink? GetAGUIEventSink(this WorkflowExecutionProjectionContext context) =>
        context.TryGetProperty<IAGUIEventSink>(AGUIEventSinkProperty, out var sink) ? sink : null;
}
