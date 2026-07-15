using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkApprovalsActTool : AgentToolBase<LarkApprovalsActTool.Parameters>
{
    private static readonly HashSet<string> AllowedActions =
    [
        "approve",
        "reject",
        "transfer",
    ];

    private readonly ILarkNyxClient _client;

    public LarkApprovalsActTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_approvals_act";

    public override string Description =>
        "Act on a Lark approval task through Nyx-backed transport as the current operator " +
        "(the user behind this turn; their identity is taken from the channel context, not from arguments). " +
        "Supports approve, reject, and transfer for a known approval_code + instance_code + task_id triple; " +
        "approval_code comes from lark_approvals_get (approval_code) or lark_approvals_list (definition_code).";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    // Approve/reject/transfer irreversibly advance someone's approval flow through an
    // org-shared tenant credential — never let the Auto classifier wave this through.
    public override bool IsDestructive => true;
    public override bool? RequiresApproval(string argumentsJson) => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.CredentialRef;
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var action = (parameters.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedActions.Contains(action))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "action must be one of: approve, reject, transfer",
            });
        }

        var approvalCode = parameters.ApprovalCode?.Trim();
        if (string.IsNullOrWhiteSpace(approvalCode))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "approval_code is required. Read it from lark_approvals_get (approval_code) or lark_approvals_list (definition_code)." });

        var instanceCode = parameters.InstanceCode?.Trim();
        if (string.IsNullOrWhiteSpace(instanceCode))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "instance_code is required." });

        var taskId = parameters.TaskId?.Trim();
        if (string.IsNullOrWhiteSpace(taskId))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "task_id is required." });

        var transferUserId = parameters.TransferUserId?.Trim();
        if (action == "transfer" && string.IsNullOrWhiteSpace(transferUserId))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "transfer_user_id is required when action=transfer." });
        if (action != "transfer" && !string.IsNullOrWhiteSpace(transferUserId))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "transfer_user_id is only allowed when action=transfer." });

        if (!string.IsNullOrWhiteSpace(parameters.FormJson))
        {
            if (action != "approve")
                return LarkProxyResponseParser.Serialize(new { success = false, error = "form_json is only supported when action=approve." });

            try
            {
                using var _ = JsonDocument.Parse(parameters.FormJson);
            }
            catch (JsonException ex)
            {
                return LarkProxyResponseParser.Serialize(new { success = false, error = $"form_json is not valid JSON: {ex.Message}" });
            }
        }

        var operatorIdentity = LarkApprovalOperatorIdentity.Resolve();
        if (operatorIdentity == null)
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "No Lark operator identity in the current channel context. Approval actions can only run as the user behind this turn.",
            });
        }

        var response = await _client.ActOnApprovalTaskAsync(
            token,
            new LarkApprovalTaskActionRequest(
                Action: action,
                ApprovalCode: approvalCode,
                InstanceCode: instanceCode,
                TaskId: taskId,
                UserId: operatorIdentity.UserId,
                Comment: parameters.Comment,
                FormJson: parameters.FormJson,
                TransferUserId: transferUserId,
                UserIdType: operatorIdentity.UserIdType),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                action,
                instance_code = instanceCode,
                task_id = taskId,
            });
        }

        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            action,
            approval_code = approvalCode,
            instance_code = instanceCode,
            task_id = taskId,
            user_id = operatorIdentity.UserId,
            transfer_user_id = transferUserId,
        });
    }

    public sealed class Parameters
    {
        public string? Action { get; set; }
        public string? ApprovalCode { get; set; }
        public string? InstanceCode { get; set; }
        public string? TaskId { get; set; }
        public string? Comment { get; set; }
        public string? FormJson { get; set; }
        public string? TransferUserId { get; set; }
    }
}
