using Aevatar.Workflow.Projection;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public static class WorkflowExecutionProjectionContextAGUIExtensions
{
    private const string AGUIEventSinkProperty = "aevatar.projection.agui.sink";

    public static void SetAGUIEventSink(this WorkflowExecutionProjectionContext context, IAGUIEventSink sink) =>
        context.SetProperty(AGUIEventSinkProperty, sink);

    public static IAGUIEventSink? GetAGUIEventSink(this WorkflowExecutionProjectionContext context) =>
        context.TryGetProperty<IAGUIEventSink>(AGUIEventSinkProperty, out var sink) ? sink : null;

    public static void DetachAGUIEventSink(this WorkflowExecutionProjectionContext context) =>
        context.RemoveProperty(AGUIEventSinkProperty);
}
