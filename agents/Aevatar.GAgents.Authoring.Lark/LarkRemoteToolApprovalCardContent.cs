using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Authoring.Lark;

public static class LarkRemoteToolApprovalCardContent
{
    public static MessageContent BuildIntent(RemoteToolApprovalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var card = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "Tool Approval Required",
            Text = BuildMarkdown(notification),
        };
        card.Actions.Add(BuildDecisionAction("nyxid-approval-approve", "Approve", true, true, false, notification));
        card.Actions.Add(BuildDecisionAction("nyxid-approval-reject", "Reject", false, false, true, notification));

        var content = new MessageContent { Text = string.Empty };
        content.Cards.Add(card);
        return content;
    }

    private static ActionElement BuildDecisionAction(
        string actionId,
        string label,
        bool approved,
        bool primary,
        bool danger,
        RemoteToolApprovalNotification notification) =>
        new()
        {
            Kind = ActionElementKind.Button,
            ActionId = actionId,
            Label = label,
            IsPrimary = primary,
            IsDanger = danger,
            NyxIdApproval = new NyxIdApprovalActionPayload
            {
                RequestId = notification.Submission.RemoteApprovalId,
                Approved = approved,
            },
        };

    private static string BuildMarkdown(RemoteToolApprovalNotification notification)
    {
        var request = notification.Request;
        var lines = new List<string>
        {
            $"Tool: `{request.ToolName}`",
            $"Request: `{notification.Submission.RemoteApprovalId}`",
            $"Local request: `{request.RequestId}`",
            $"Destructive: `{request.IsDestructive.ToString().ToLowerInvariant()}`",
        };

        if (notification.Submission.ExpiresAt is { } expiresAt)
            lines.Add($"Expires: `{expiresAt:O}`");

        if (!string.IsNullOrWhiteSpace(request.ArgumentsJson))
        {
            lines.Add(string.Empty);
            lines.Add("Arguments:");
            lines.Add($"```json\n{request.ArgumentsJson}\n```");
        }

        return string.Join('\n', lines);
    }

}
