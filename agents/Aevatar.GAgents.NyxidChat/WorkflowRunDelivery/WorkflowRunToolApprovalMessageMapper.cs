using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;

internal static class WorkflowRunToolApprovalMessageMapper
{
    public static MessageContent ToMessageContent(WorkflowRunToolApprovalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var card = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "Workflow Tool Approval Required",
            Text = BuildText(notification),
        };
        card.Actions.Add(BuildDecisionAction(
            "workflow-tool-approval-approve",
            "Approve",
            approved: true,
            primary: true,
            danger: false,
            notification));
        card.Actions.Add(BuildDecisionAction(
            "workflow-tool-approval-reject",
            "Reject",
            approved: false,
            primary: false,
            danger: true,
            notification));

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
        WorkflowRunToolApprovalNotification notification) =>
        new()
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = actionId,
            Label = label,
            IsPrimary = primary,
            IsDanger = danger,
            WorkflowResume = new WorkflowResumeActionPayload
            {
                ActorId = notification.WorkflowActorId,
                RunId = notification.WorkflowRunId,
                StepId = notification.StepId,
                Approved = approved,
                ToolApproval = new WorkflowToolApprovalResumeActionPayload
                {
                    ExecutionId = notification.ExecutionId,
                    ToolCallId = notification.ToolCallId,
                    ApprovalRequestId = notification.ApprovalRequestId,
                },
            },
        };

    private static string BuildText(WorkflowRunToolApprovalNotification notification)
    {
        var prompt = string.IsNullOrWhiteSpace(notification.Prompt)
            ? $"Approve tool `{notification.ToolName}` execution?"
            : notification.Prompt.Trim();
        return $"{prompt}\nTool: `{notification.ToolName}`";
    }
}
