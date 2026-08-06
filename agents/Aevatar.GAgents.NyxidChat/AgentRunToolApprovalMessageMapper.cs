using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

internal static class AgentRunToolApprovalMessageMapper
{
    public static MessageContent ToMessageContent(AgentRunPendingToolApprovalState pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var card = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "Tool Approval Required",
            Text = BuildText(pending),
        };
        card.Actions.Add(BuildDecisionAction(
            "agent-run-approval-approve",
            "Approve",
            approved: true,
            primary: true,
            danger: false,
            pending));
        card.Actions.Add(BuildDecisionAction(
            "agent-run-approval-reject",
            "Reject",
            approved: false,
            primary: false,
            danger: true,
            pending));

        var content = new MessageContent();
        content.Cards.Add(card);
        return content;
    }

    private static ActionElement BuildDecisionAction(
        string actionId,
        string label,
        bool approved,
        bool primary,
        bool danger,
        AgentRunPendingToolApprovalState pending) =>
        new()
        {
            Kind = ActionElementKind.Button,
            ActionId = actionId,
            Label = label,
            IsPrimary = primary,
            IsDanger = danger,
            AgentRunApproval = new AgentRunApprovalActionPayload
            {
                RunId = pending.RunId,
                ApprovalRequestId = pending.ApprovalRequestId,
                ToolCallId = pending.ToolCallId,
                ToolName = pending.ToolName,
                ArgumentsSha256 = pending.ArgumentsSha256,
                Approved = approved,
            },
        };

    private static string BuildText(AgentRunPendingToolApprovalState pending)
    {
        var lines = new List<string>
        {
            $"Tool: `{pending.ToolName}`",
            $"Request: `{pending.ApprovalRequestId}`",
            $"Destructive: `{pending.IsDestructive.ToString().ToLowerInvariant()}`",
        };
        if (!string.IsNullOrWhiteSpace(pending.SideEffectKind))
            lines.Add($"Effect: `{pending.SideEffectKind}`");
        if (!string.IsNullOrWhiteSpace(pending.SubjectKind) || !string.IsNullOrWhiteSpace(pending.SubjectId))
            lines.Add($"Target: `{BuildTarget(pending)}`");
        return string.Join('\n', lines);
    }

    private static string BuildTarget(AgentRunPendingToolApprovalState pending) =>
        (pending.SubjectKind, pending.SubjectId) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{pending.SubjectKind}:{pending.SubjectId}",
            (_, { Length: > 0 }) => pending.SubjectId,
            ({ Length: > 0 }, _) => pending.SubjectKind,
            _ => pending.ToolName,
        };
}
