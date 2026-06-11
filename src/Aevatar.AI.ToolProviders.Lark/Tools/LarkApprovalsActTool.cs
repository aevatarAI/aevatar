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

    private static readonly HashSet<string> AllowedUserIdTypes =
    [
        "user_id",
        "union_id",
        "open_id",
    ];

    private readonly ILarkNyxClient _client;

    public LarkApprovalsActTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_approvals_act";

    public override string Description =>
        "Act on a Lark approval task through Nyx-backed transport. " +
        "Supports approve, reject, and transfer for a known instance_code + task_id pair.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
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

        var instanceCode = parameters.InstanceCode?.Trim();
        if (string.IsNullOrWhiteSpace(instanceCode))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "instance_code is required." });

        var taskId = parameters.TaskId?.Trim();
        if (string.IsNullOrWhiteSpace(taskId))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "task_id is required." });

        var userIdType = parameters.UserIdType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(userIdType) && !AllowedUserIdTypes.Contains(userIdType))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "user_id_type must be one of: user_id, union_id, open_id" });

        var userId = ResolveUserId(parameters.UserId);
        if (string.IsNullOrWhiteSpace(userId))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "user_id is required. Use the Lark approval operator user_id from current turn context." });

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

        var response = await _client.ActOnApprovalTaskAsync(
            token,
            new LarkApprovalTaskActionRequest(
                Action: action,
                InstanceCode: instanceCode,
                TaskId: taskId,
                UserId: userId,
                Comment: parameters.Comment,
                FormJson: parameters.FormJson,
                TransferUserId: transferUserId,
                UserIdType: userIdType),
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
            instance_code = instanceCode,
            task_id = taskId,
            user_id = userId,
            transfer_user_id = transferUserId,
        });
    }

    private static string? ResolveUserId(string? explicitUserId)
    {
        var operatorUserId = AgentToolRequestContext.TryGetExternalMetadata("channel.lark.operator_user_id")?.Trim();
        if (!string.IsNullOrWhiteSpace(operatorUserId))
            return operatorUserId;

        var normalized = explicitUserId?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return null;
    }

    public sealed class Parameters
    {
        public string? Action { get; set; }
        public string? InstanceCode { get; set; }
        public string? TaskId { get; set; }
        public string? UserId { get; set; }
        public string? Comment { get; set; }
        public string? FormJson { get; set; }
        public string? TransferUserId { get; set; }
        public string? UserIdType { get; set; }
    }
}
