using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkApprovalsGetTool : AgentToolBase<LarkApprovalsGetTool.Parameters>
{
    private static readonly HashSet<string> AllowedLocales =
    [
        "zh-CN",
        "en-US",
        "ja-JP",
    ];

    private static readonly HashSet<string> AllowedUserIdTypes =
    [
        "user_id",
        "union_id",
        "open_id",
    ];

    private readonly ILarkNyxClient _client;

    public LarkApprovalsGetTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_approvals_get";

    public override string Description =>
        "Read one Lark approval instance by instance_code through Nyx-backed transport. " +
        "Returns normalized terminal status and stable control-flow fields for workflow waiting.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
    public override bool IsReadOnly => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var instanceCode = parameters.InstanceCode?.Trim();
        if (string.IsNullOrWhiteSpace(instanceCode))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "instance_code is required." });

        var locale = parameters.Locale?.Trim();
        if (!string.IsNullOrWhiteSpace(locale) && !AllowedLocales.Contains(locale))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "locale must be one of: zh-CN, en-US, ja-JP",
            });
        }

        var userIdType = parameters.UserIdType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(userIdType) && !AllowedUserIdTypes.Contains(userIdType))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "user_id_type must be one of: user_id, union_id, open_id",
            });
        }

        var response = await _client.GetApprovalInstanceAsync(
            token,
            new LarkApprovalInstanceGetRequest(
                InstanceCode: instanceCode,
                Locale: locale,
                UserIdType: userIdType),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                instance_code = instanceCode,
            });
        }

        var result = LarkProxyResponseParser.ParseApprovalInstanceSuccess(response);
        var status = NormalizeInstanceStatus(result.Status);
        var terminal = IsTerminalStatus(status);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            instance_code = result.InstanceCode ?? instanceCode,
            approval_code = result.ApprovalCode,
            approval_name = result.ApprovalName,
            status,
            raw_status = result.Status,
            is_terminal = terminal,
            terminal_status = terminal ? status : null,
            should_continue_waiting = !terminal,
            approved = status == "approved",
            rejected = status == "rejected",
            withdrawn = status == "withdrawn",
            terminated = status == "terminated",
            start_time = result.StartTime,
            end_time = result.EndTime,
            serial_number = result.SerialNumber,
            user_id = result.UserId,
            user_name = result.UserName,
            department_id = result.DepartmentId,
            department_name = result.DepartmentName,
            uuid = result.Uuid,
            task_count = result.TaskList.Count,
            tasks = result.TaskList.Select(task => new
            {
                task_id = task.TaskId,
                user_id = task.UserId,
                user_name = task.UserName,
                status = NormalizeTaskStatus(task.Status),
                raw_status = task.Status,
                start_time = task.StartTime,
                end_time = task.EndTime,
            }).ToArray(),
            form = result.Form.Select(field => new
            {
                id = field.Id,
                name = field.Name,
                type = field.Type,
                value = field.Value,
                ext = field.Ext,
            }).ToArray(),
        });
    }

    private static bool IsTerminalStatus(string? status) =>
        status is "approved" or "rejected" or "withdrawn" or "terminated";

    private static string? NormalizeInstanceStatus(string? value) =>
        value switch
        {
            "0" => "none",
            "1" => "running",
            "2" => "approved",
            "3" => "rejected",
            "4" => "withdrawn",
            "5" => "terminated",
            _ => value,
        };

    private static string? NormalizeTaskStatus(string? value) =>
        value switch
        {
            "1" => "todo",
            "2" => "done",
            "17" => "unread",
            "18" => "read",
            "33" => "processing",
            "34" => "withdrawn",
            _ => value,
        };

    public sealed class Parameters
    {
        public string? InstanceCode { get; set; }
        public string? Locale { get; set; }
        public string? UserIdType { get; set; }
    }
}
