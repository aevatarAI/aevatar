using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Primitives;

internal static class WorkflowSuspensionRequestSupport
{
    public static void ApplyDeliveryTarget(
        WorkflowSuspendedEvent suspended,
        StepRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(suspended);
        ArgumentNullException.ThrowIfNull(request);

        var deliveryTargetId = WorkflowParameterValueParser.GetOptionalString(
            request.Parameters,
            "delivery_target_id",
            "deliveryTargetId");
        if (!string.IsNullOrWhiteSpace(deliveryTargetId))
            suspended.DeliveryTargetId = deliveryTargetId.Trim();
    }

    public static void ApplyContent(
        WorkflowSuspendedEvent suspended,
        string? content)
    {
        ArgumentNullException.ThrowIfNull(suspended);

        if (string.IsNullOrWhiteSpace(content))
            return;

        suspended.Content = content;
    }
}
