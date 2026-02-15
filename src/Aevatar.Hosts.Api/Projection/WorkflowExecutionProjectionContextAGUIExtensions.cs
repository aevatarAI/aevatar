using Aevatar.CQRS.Projections.Abstractions;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Hosts.Api.Projection;

internal static class WorkflowExecutionProjectionContextAGUIExtensions
{
    private const string AGUIEventSinkProperty = "aevatar.projection.agui.sink";

    public static void SetAGUIEventSink(this WorkflowExecutionProjectionContext context, IAGUIEventSink sink) =>
        context.SetProperty(AGUIEventSinkProperty, sink);

    public static IAGUIEventSink? GetAGUIEventSink(this WorkflowExecutionProjectionContext context) =>
        context.TryGetProperty<IAGUIEventSink>(AGUIEventSinkProperty, out var sink) ? sink : null;
}
