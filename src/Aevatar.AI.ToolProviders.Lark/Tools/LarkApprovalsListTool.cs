using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkApprovalsListTool : AgentToolBase<LarkApprovalsListTool.Parameters>
{
    private static readonly Dictionary<string, string> TopicAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = "1",
        ["todo"] = "1",
        ["pending"] = "1",
        ["2"] = "2",
        ["done"] = "2",
        ["completed"] = "2",
        ["3"] = "3",
        ["initiated"] = "3",
        ["started"] = "3",
        ["17"] = "17",
        ["cc_unread"] = "17",
        ["unread_cc"] = "17",
        ["18"] = "18",
        ["cc_read"] = "18",
        ["read_cc"] = "18",
    };

    private readonly ILarkNyxClient _client;

    public LarkApprovalsListTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_approvals_list";

    public override string Description =>
        "List the Lark approval tasks of the current operator (the user behind this turn; " +
        "their identity is taken from the channel context, not from arguments). " +
        "Use this to discover pending, completed, initiated, or CC approval work before acting on a task.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
    public override bool IsReadOnly => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        if (!TryNormalizeTopic(parameters.Topic, out var topic))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "topic must be one of: todo, done, initiated, cc_unread, cc_read",
            });
        }

        var pageSize = parameters.PageSize is > 0 ? parameters.PageSize.Value : 20;
        if (pageSize is < 1 or > 100)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "page_size must be between 1 and 100." });

        var operatorIdentity = LarkApprovalOperatorIdentity.Resolve();
        if (operatorIdentity == null)
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "No Lark operator identity in the current channel context. Approval tasks can only be listed for the user behind this turn.",
            });
        }

        var response = await _client.ListApprovalTasksAsync(
            token,
            new LarkApprovalTaskQueryRequest(
                Topic: topic!,
                UserId: operatorIdentity.UserId,
                PageSize: pageSize,
                PageToken: parameters.PageToken?.Trim(),
                UserIdType: operatorIdentity.UserIdType),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
            return LarkProxyResponseParser.Serialize(new { success = false, error });

        var result = LarkProxyResponseParser.ParseApprovalTaskQuerySuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            count = result.Count,
            has_more = result.HasMore,
            page_token = result.PageToken,
            tasks = result.Tasks.Select(task => new
            {
                task_id = task.TaskId,
                instance_code = task.ProcessCode,
                process_id = task.ProcessId,
                title = task.Title,
                status = NormalizeTaskStatus(task.Status),
                process_status = NormalizeProcessStatus(task.ProcessStatus),
                topic = NormalizeTopicValue(task.Topic),
                definition_code = task.DefinitionCode,
                definition_name = task.DefinitionName,
                initiators = task.Initiators,
                initiator_names = task.InitiatorNames,
                user_id = task.UserId,
                link = task.Link,
            }).ToArray(),
        });
    }

    private static bool TryNormalizeTopic(string? input, out string? topic)
    {
        topic = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        return TopicAliases.TryGetValue(input.Trim(), out topic);
    }

    private static string? NormalizeTopicValue(string? value) =>
        value switch
        {
            "1" => "todo",
            "2" => "done",
            "3" => "initiated",
            "17" => "cc_unread",
            "18" => "cc_read",
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

    private static string? NormalizeProcessStatus(string? value) =>
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

    public sealed class Parameters
    {
        public string? Topic { get; set; }
        public int? PageSize { get; set; }
        public string? PageToken { get; set; }
    }
}
