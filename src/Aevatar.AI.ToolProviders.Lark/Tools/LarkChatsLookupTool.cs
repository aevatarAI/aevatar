using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkChatsLookupTool : AgentToolBase<LarkChatsLookupTool.Parameters>
{
    private static readonly HashSet<string> AllowedSearchTypes =
    [
        "private",
        "external",
        "public_joined",
        "public_not_joined",
    ];

    private readonly ILarkNyxClient _client;

    public LarkChatsLookupTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_chats_lookup";

    public override string Description =>
        "Search Lark chats visible to the current Nyx-backed identity. " +
        "Use this to resolve chat IDs before proactive sends.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
    public override bool IsReadOnly => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var query = parameters.Query?.Trim();
        var memberIds = parameters.MemberIds?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(query) && (memberIds is null || memberIds.Length == 0))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "At least one of query or member_ids is required.",
            });
        }

        if (!string.IsNullOrWhiteSpace(query) && query.Length > 64)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "query exceeds the maximum of 64 characters." });

        if (memberIds is { Length: > 50 })
            return LarkProxyResponseParser.Serialize(new { success = false, error = "member_ids exceeds the maximum of 50 values." });

        var searchTypes = parameters.SearchTypes?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (searchTypes is not null)
        {
            var invalid = searchTypes.Where(value => !AllowedSearchTypes.Contains(value)).ToArray();
            if (invalid.Length > 0)
            {
                return LarkProxyResponseParser.Serialize(new
                {
                    success = false,
                    error = $"search_types contains invalid values: {string.Join(", ", invalid)}",
                });
            }
        }

        var pageSize = parameters.PageSize is > 0 ? parameters.PageSize.Value : 20;
        if (pageSize is < 1 or > 100)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "page_size must be between 1 and 100." });

        var response = await _client.SearchChatsAsync(
            token,
            new LarkChatSearchRequest(
                Query: query,
                MemberIds: memberIds,
                SearchTypes: searchTypes,
                IsManager: parameters.IsManager ?? false,
                DisableSearchByUser: parameters.DisableSearchByUser ?? false,
                PageSize: pageSize,
                PageToken: parameters.PageToken?.Trim()),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
            return LarkProxyResponseParser.Serialize(new { success = false, error });

        var result = LarkProxyResponseParser.ParseChatSearchSuccess(
            response,
            query,
            parameters.ExactMatchHint ?? false);

        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            count = result.Chats.Count,
            total = result.Total,
            has_more = result.HasMore,
            page_token = result.PageToken,
            chats = result.Chats.Select(chat => new
            {
                chat_id = chat.ChatId,
                title = chat.Title,
                description = chat.Description,
                chat_mode = chat.ChatMode,
                chat_status = chat.ChatStatus,
                owner_id = chat.OwnerId,
                external = chat.External,
                exact_name_match = chat.ExactNameMatch,
            }).ToArray(),
        });
    }

    public sealed class Parameters
    {
        public string? Query { get; set; }
        public List<string>? MemberIds { get; set; }
        public List<string>? SearchTypes { get; set; }
        public bool? IsManager { get; set; }
        public bool? DisableSearchByUser { get; set; }
        public int? PageSize { get; set; }
        public string? PageToken { get; set; }
        public bool? ExactMatchHint { get; set; }
    }
}
