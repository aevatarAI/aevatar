namespace Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;

internal static class WorkflowRunDeliveryActorIds
{
    public static string FromWorkflowCommand(string workflowActorId, string workflowCommandId)
    {
        var actor = NormalizeSegment(workflowActorId);
        var command = NormalizeSegment(workflowCommandId);
        return $"workflow-run-delivery:{actor}:{command}";
    }

    private static string NormalizeSegment(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "missing" : value.Trim();
        var chars = normalized
            .Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
            .ToArray();
        return new string(chars);
    }
}
