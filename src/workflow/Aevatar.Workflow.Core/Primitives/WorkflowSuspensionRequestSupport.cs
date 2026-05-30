using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Primitives;

internal static class WorkflowSuspensionRequestSupport
{
    // Refactor (issue1372): Old pattern: suspension delivery lookup only accepted delivery_target_id.
    // New principle: preserve delivery_target_id precedence while treating delivery_agent_id as a legacy fallback.
    public static string? ResolveDeliveryTargetId(StepRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deliveryTargetId = WorkflowParameterValueParser.GetOptionalString(
            request.Parameters,
            "delivery_target_id",
            "deliveryTargetId",
            "delivery_agent_id",
            "deliveryAgentId");
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
