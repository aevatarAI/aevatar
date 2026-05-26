using System.Net;
using System.Reflection;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Lark.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Lark.Tests;

public sealed class LarkCoverageTests
{
    [Fact]
    public void ToolMetadata_ShouldExposeStableContracts()
    {
        var client = new StubLarkNyxClient();

        var sendTool = new LarkMessagesSendTool(client);
        sendTool.Name.Should().Be("lark_messages_send");
        sendTool.Description.Should().Contain("Proactively send a Lark message");
        sendTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);

        var replyTool = new LarkMessagesReplyTool(client);
        replyTool.Name.Should().Be("lark_messages_reply");
        replyTool.Description.Should().Contain("Reply to a specific Lark message");
        replyTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);

        var reactTool = new LarkMessagesReactTool(client);
        reactTool.Name.Should().Be("lark_messages_react");
        reactTool.Description.Should().Contain("Add an emoji reaction");
        reactTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);

        var reactionsListTool = new LarkMessagesReactionsListTool(client);
        reactionsListTool.Name.Should().Be("lark_messages_reactions_list");
        reactionsListTool.Description.Should().Contain("List emoji reaction records");
        reactionsListTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        reactionsListTool.IsReadOnly.Should().BeTrue();

        var reactionsDeleteTool = new LarkMessagesReactionsDeleteTool(client);
        reactionsDeleteTool.Name.Should().Be("lark_messages_reactions_delete");
        reactionsDeleteTool.Description.Should().Contain("Delete a specific Lark message reaction");
        reactionsDeleteTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);

        var searchTool = new LarkMessagesSearchTool(client);
        searchTool.Name.Should().Be("lark_messages_search");
        searchTool.Description.Should().Contain("Search Lark messages");
        searchTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        searchTool.IsReadOnly.Should().BeTrue();

        var batchGetTool = new LarkMessagesBatchGetTool(client);
        batchGetTool.Name.Should().Be("lark_messages_batch_get");
        batchGetTool.Description.Should().Contain("Batch fetch full Lark message details");
        batchGetTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        batchGetTool.IsReadOnly.Should().BeTrue();

        var lookupTool = new LarkChatsLookupTool(client);
        lookupTool.Name.Should().Be("lark_chats_lookup");
        lookupTool.Description.Should().Contain("Search Lark chats");
        lookupTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        lookupTool.IsReadOnly.Should().BeTrue();

        var approvalsListTool = new LarkApprovalsListTool(client);
        approvalsListTool.Name.Should().Be("lark_approvals_list");
        approvalsListTool.Description.Should().Contain("List approval tasks");
        approvalsListTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        approvalsListTool.IsReadOnly.Should().BeTrue();

        var approvalsActTool = new LarkApprovalsActTool(client);
        approvalsActTool.Name.Should().Be("lark_approvals_act");
        approvalsActTool.Description.Should().Contain("Act on a Lark approval task");
        approvalsActTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);

        var sheetsTool = new LarkSheetsAppendRowsTool(client);
        sheetsTool.Name.Should().Be("lark_sheets_append_rows");
        sheetsTool.Description.Should().Contain("Append rows to a known Lark spreadsheet");
        sheetsTool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
    }

    [Fact]
    public async Task LarkAgentToolSource_ShouldSkipTools_WhenProviderSlugMissing()
    {
        var source = new LarkAgentToolSource(
            new LarkToolOptions { ProviderSlug = "   " },
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new StubLarkNyxClient());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public void AddLarkTools_ShouldRegisterServices()
    {
        var services = new ServiceCollection();

        services.AddLarkTools(options =>
        {
            options.ProviderSlug = "custom-provider";
            options.EnableApprovalsAct = false;
        });

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(LarkToolOptions) &&
            descriptor.ImplementationInstance is LarkToolOptions);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(NyxIdToolOptions) &&
            descriptor.ImplementationType == typeof(NyxIdToolOptions));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(NyxIdApiClient) &&
            descriptor.ImplementationType == typeof(NyxIdApiClient));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(ILarkNyxClient) &&
            descriptor.ImplementationType == typeof(LarkNyxClient));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolSource) &&
            descriptor.ImplementationType == typeof(LarkAgentToolSource));

        services.Single(descriptor => descriptor.ServiceType == typeof(LarkToolOptions))
            .ImplementationInstance.Should().BeOfType<LarkToolOptions>()
            .Which.ProviderSlug.Should().Be("custom-provider");
    }

    [Fact]
    public void LarkProxyResponseParser_ShouldHandleErrorShapes()
    {
        InvokeTryParseError(null).Should().Be((true, "empty_lark_response"));

        InvokeTryParseError("""{"error":true,"status":502,"message":"gateway","body":"denied"}""")
            .Should()
            .BeEquivalentTo((true, "nyx_proxy_error status=502 message=gateway body=denied"));

        InvokeTryParseError("""{"code":9301,"data":{"msg":"blocked"}}""")
            .Should()
            .BeEquivalentTo((true, "lark_code=9301 msg=blocked"));

        InvokeTryParseError("""{"code":0}""").Should().Be((false, string.Empty));
        InvokeTryParseError("not-json").Should().Be((true, "invalid_lark_response_json"));
    }

    [Fact]
    public void LarkSheetsRangeHelper_ShouldNormalizeTokensAndRanges()
    {
        InvokeExtractSpreadsheetToken("https://example.feishu.cn/sheets/shtcn_123?from=1")
            .Should()
            .Be("shtcn_123");
        InvokeExtractSpreadsheetToken("sheet-direct-token").Should().Be("sheet-direct-token");

        var missing = InvokeTryResolveAppendRange(null, null);
        missing.Success.Should().BeFalse();
        missing.Error.Should().Be("sheet_id or range is required.");

        var sheetOnly = InvokeTryResolveAppendRange("sheet_1", null);
        sheetOnly.Success.Should().BeTrue();
        sheetOnly.Resolved.Should().Be("sheet_1");

        var malformedRange = InvokeTryResolveAppendRange(null, "sheet_1!");
        malformedRange.Success.Should().BeTrue();
        malformedRange.Resolved.Should().Be("sheet_1!");

        var explicitSpan = InvokeTryResolveAppendRange(null, "sheet_1!A1:B2");
        explicitSpan.Success.Should().BeTrue();
        explicitSpan.Resolved.Should().Be("sheet_1!A1:B2");
    }

    [Fact]
    public async Task LarkNyxClient_ShouldCoverAdditionalTransportBranches()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LarkNyxClient(
            new LarkToolOptions { ProviderSlug = "api-lark-bot" },
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

        await client.SearchChatsAsync(
            "token-123",
            new LarkChatSearchRequest("teamalpha", null, null, false, true, 20, null),
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should()
            .Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v2/chats/search?page_size=20");
        handler.LastBody.Should().Contain("\"query\":\"teamalpha\"");
        handler.LastBody.Should().Contain("\"disable_search_by_user\":true");

        await client.ActOnApprovalTaskAsync(
            "token-123",
            new LarkApprovalTaskActionRequest(
                "approve",
                "inst-1",
                "task-1",
                "looks good",
                """{"field":"value"}""",
                null,
                null),
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should()
            .Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks/pass");
        handler.LastBody.Should().Contain("\"comment\":\"looks good\"");
        handler.LastBody.Should().Contain("\"form\":\"{\\u0022field\\u0022:\\u0022value\\u0022}\"");

        await client.ActOnApprovalTaskAsync(
            "token-123",
            new LarkApprovalTaskActionRequest("reject", "inst-1", "task-1", null, null, null, null),
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should()
            .Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks/refuse");

        await client.ActOnApprovalTaskAsync(
            "token-123",
            new LarkApprovalTaskActionRequest("transfer", "inst-1", "task-1", null, null, "ou_target", null),
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should()
            .Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/approval/v4/tasks/forward");

        var unsupported = () => client.ActOnApprovalTaskAsync(
            "token-123",
            new LarkApprovalTaskActionRequest("escalate", "inst-1", "task-1", null, null, null, null),
            CancellationToken.None);

        await unsupported.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unsupported approval action: escalate");
    }

    private static (bool Matched, string Error) InvokeTryParseError(string? response)
    {
        var parserType = typeof(LarkAgentToolSource).Assembly.GetType("Aevatar.AI.ToolProviders.Lark.LarkProxyResponseParser")!;
        var method = parserType.GetMethod("TryParseError", BindingFlags.Public | BindingFlags.Static)!;
        object?[] args = [response, null];
        var matched = (bool)method.Invoke(null, args)!;
        return (matched, (string)args[1]!);
    }

    private static string InvokeExtractSpreadsheetToken(string value)
    {
        var helperType = typeof(LarkAgentToolSource).Assembly.GetType("Aevatar.AI.ToolProviders.Lark.LarkSheetsRangeHelper")!;
        var method = helperType.GetMethod("ExtractSpreadsheetToken", BindingFlags.Public | BindingFlags.Static)!;
        return (string)method.Invoke(null, [value])!;
    }

    private static (bool Success, string? Resolved, string? Error) InvokeTryResolveAppendRange(string? sheetId, string? range)
    {
        var helperType = typeof(LarkAgentToolSource).Assembly.GetType("Aevatar.AI.ToolProviders.Lark.LarkSheetsRangeHelper")!;
        var method = helperType.GetMethod("TryResolveAppendRange", BindingFlags.Public | BindingFlags.Static)!;
        object?[] args = [sheetId, range, null, null];
        var success = (bool)method.Invoke(null, args)!;
        return (success, (string?)args[2], (string?)args[3]);
    }

    private sealed class StubLarkNyxClient : ILarkNyxClient
    {
        public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{}}""");
        }

        public Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{}}""");
        }

        public Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{}}""");
        }

        public Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{"items":[]}}""");
        }

        public Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{}}""");
        }

        public Task<string> SearchMessagesAsync(string token, LarkMessageSearchRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{"items":[],"count":0}}""");
        }

        public Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{"items":[]}}""");
        }

        public Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{"items":[],"total":0}}""");
        }

        public Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{"updates":{}}}""");
        }

        public Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{"tasks":[],"count":0}}""");
        }

        public Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct)
        {
            _ = token;
            _ = request;
            _ = ct;
            return Task.FromResult("""{"code":0,"data":{}}""");
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}
