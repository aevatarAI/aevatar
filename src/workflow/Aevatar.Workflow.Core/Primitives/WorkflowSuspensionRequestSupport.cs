using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Primitives;

internal static class WorkflowSuspensionRequestSupport
{
    public static string? ResolveDeliveryTargetId(StepRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deliveryTargetId = WorkflowParameterValueParser.GetOptionalString(
            request.Parameters,
            "delivery_target_id",
            "deliveryTargetId");
        return string.IsNullOrWhiteSpace(deliveryTargetId)
            ? null
            : deliveryTargetId.Trim();
    }

    public static void ApplyDeliveryTarget(
        WorkflowSuspendedEvent suspended,
        StepRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(suspended);
        ArgumentNullException.ThrowIfNull(request);

        var deliveryTargetId = ResolveDeliveryTargetId(request);
        if (!string.IsNullOrWhiteSpace(deliveryTargetId))
            suspended.DeliveryTargetId = deliveryTargetId;
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
