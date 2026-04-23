using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkApprovalsListTool : AgentToolBase<LarkApprovalsListTool.Parameters>
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
        "List approval tasks visible to the current Nyx-backed Lark identity. " +
        "Use this to discover pending, completed, initiated, or CC approval work before acting on a task.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
    public override bool IsReadOnly => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
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

        var pageSize = parameters.PageSize is > 0 ? parameters.PageSize.Value : 20;
        if (pageSize is < 1 or > 100)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "page_size must be between 1 and 100." });

        var response = await _client.ListApprovalTasksAsync(
            token,
            new LarkApprovalTaskQueryRequest(
                Topic: topic!,
                DefinitionCode: parameters.DefinitionCode?.Trim(),
                Locale: locale,
                PageSize: pageSize,
                PageToken: parameters.PageToken?.Trim(),
                UserIdType: userIdType),
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
                instance_code = task.InstanceCode,
                title = task.Title,
                status = NormalizeTaskStatus(task.Status),
                topic = NormalizeTopicValue(task.Topic),
                support_api_operate = task.SupportApiOperate,
                definition_code = task.DefinitionCode,
                definition_name = task.DefinitionName,
                initiator = task.Initiator,
                initiator_name = task.InitiatorName,
                user_id = task.UserId,
                instance_status = NormalizeInstanceStatus(task.InstanceStatus),
                link = task.Link,
                summaries = task.Summaries.Select(summary => new
                {
                    key = summary.Key,
                    value = summary.Value,
                }).ToArray(),
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

    public sealed class Parameters
    {
        public string? Topic { get; set; }
        public string? DefinitionCode { get; set; }
        public string? Locale { get; set; }
        public int? PageSize { get; set; }
        public string? PageToken { get; set; }
        public string? UserIdType { get; set; }
    }
}
