using System.Net;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdServicesToolTests
{
    [Fact]
    public async Task CreateResultReceipt_ListSuccess_ShouldReturnTypedSuccess()
    {
        using var client = CreateClient(new FixedResponseHandler(
            HttpStatusCode.OK,
            """{"keys":[{"id":"usvc-alpha","slug":"api-github","label":"GitHub"}]}"""));
        var tool = new NyxIdServicesTool(client);
        const string arguments = """{"action":"list"}""";

        using var _scope = PushToken();
        var result = await tool.ExecuteAsync(arguments);
        var receipt = ((IAgentTool)tool).CreateResultReceipt("call-list", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ResultJson.Should().Be(result);
        receipt.ErrorCode.Should().BeEmpty();
        receipt.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateResultReceipt_ShowSuccess_ShouldReturnTypedSuccess()
    {
        using var client = CreateClient(new FixedResponseHandler(
            HttpStatusCode.OK,
            """{"id":"usvc-alpha","slug":"api-github","label":"GitHub"}"""));
        var tool = new NyxIdServicesTool(client);
        const string arguments = """{"action":"show","id":"usvc-alpha"}""";

        using var _scope = PushToken();
        var result = await tool.ExecuteAsync(arguments);
        var receipt = ((IAgentTool)tool).CreateResultReceipt("call-show", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ResultJson.Should().Be(result);
        receipt.ErrorCode.Should().BeEmpty();
        receipt.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateResultReceipt_NyxIdHttpError_ShouldReturnStableErrorReceipt()
    {
        using var client = CreateClient(new FixedResponseHandler(
            HttpStatusCode.ServiceUnavailable,
            """{"message":"upstream bearer-secret"}"""));
        var tool = new NyxIdServicesTool(client);
        const string arguments = """{"action":"list"}""";

        using var _scope = PushToken();
        var result = await tool.ExecuteAsync(arguments);
        var receipt = ((IAgentTool)tool).CreateResultReceipt("call-error", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_SERVICES_HTTP_503");
        receipt.ErrorMessage.Should().Be("The NyxID services request failed.");
        receipt.ResultJson.Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task CreateResultReceipt_NyxIdTransportException_ShouldReturnStableErrorReceipt()
    {
        using var client = CreateClient(new ThrowingHandler(
            new HttpRequestException("transport failed for bearer-secret")));
        var tool = new NyxIdServicesTool(client);
        const string arguments = """{"action":"list"}""";

        using var _scope = PushToken();
        var result = await tool.ExecuteAsync(arguments);
        var receipt = ((IAgentTool)tool).CreateResultReceipt("call-transport", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_SERVICES_TRANSPORT_FAILURE");
        receipt.ErrorMessage.Should().Be("The NyxID services request failed.");
        receipt.ResultJson.Should().NotContain("bearer-secret");
    }

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

    private static AgentToolContextScope PushToken() =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("request-token", null, null),
        });

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
