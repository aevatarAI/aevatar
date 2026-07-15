using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApprovalsToolTests
{
    [Theory]
    [InlineData("approve", true)]
    [InlineData("reject", false)]
    [InlineData("deny", false)]
    public async Task ExecuteAsync_ShouldSendNyxIdApprovedBooleanForDecisionActions(
        string action,
        bool expectedApproved)
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"approval-1","status":"approved"}""", Encoding.UTF8, "application/json"),
        });
        var tool = new NyxIdApprovalsTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance));

        using var _ = AgentToolContextScope.Push(WithNyxIdAccessToken("token-1"));
        var result = await tool.ExecuteAsync($$"""{"action":"{{action}}","id":"approval-1"}""");

        result.Should().Contain("\"status\":\"approved\"");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should()
            .Be("/api/v1/approvals/requests/approval-1/decide");

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        body.RootElement.GetProperty("approved").GetBoolean().Should().Be(expectedApproved);
        body.RootElement.TryGetProperty("decision", out var ignored).Should().BeFalse();
    }

    private static AgentToolExecutionContext WithNyxIdAccessToken(string token) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with { CredentialRef = token },
        };

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return response;
        }
    }
}
