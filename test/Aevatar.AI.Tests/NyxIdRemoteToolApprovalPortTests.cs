using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdRemoteToolApprovalPortTests
{
    [Fact]
    public async Task SubmitAsync_ShouldReturnRemoteApprovalIdAndExpiryWithoutPolling()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                """{"id":"approval-1","status":"pending","expires_at":"2026-05-21T10:30:00Z"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var port = CreatePort(handler);

        var result = await port.SubmitAsync(
            new RemoteToolApprovalRequest(
                "req-1",
                "ssh_exec",
                "call-1",
                """{"command":"uptime"}""",
                ToolApprovalMode.Auto,
                true,
                new Dictionary<string, string>
                {
                    [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-1",
                }),
            CancellationToken.None);

        result.RemoteApprovalId.Should().Be("approval-1");
        result.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-05-21T10:30:00Z"));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/approvals/requests");
        handler.Requests[0].Headers.Authorization!.ToString().Should().Be("Bearer token-1");

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        body.RootElement.GetProperty("tool_name").GetString().Should().Be("ssh_exec");
        body.RootElement.GetProperty("tool_call_id").GetString().Should().Be("call-1");
        body.RootElement.GetProperty("is_destructive").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("approved", RemoteToolApprovalStatus.Approved)]
    [InlineData("rejected", RemoteToolApprovalStatus.Rejected)]
    [InlineData("denied", RemoteToolApprovalStatus.Rejected)]
    [InlineData("pending", RemoteToolApprovalStatus.Pending)]
    [InlineData("expired", RemoteToolApprovalStatus.Expired)]
    [InlineData("unexpected", RemoteToolApprovalStatus.Unknown)]
    public async Task GetStatusAsync_ShouldCallStatusEndpointOnceAndMapStatuses(
        string rawStatus,
        RemoteToolApprovalStatus expected)
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"status":"{{rawStatus}}","reason":"because","expires_at":"2026-05-21T10:30:00Z"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var port = CreatePort(handler);

        var result = await port.GetStatusAsync(
            new RemoteToolApprovalStatusQuery(
                "req-1",
                "approval-1",
                new Dictionary<string, string>
                {
                    [LLMRequestMetadataKeys.NyxIdAccessToken] = "token-1",
                }),
            CancellationToken.None);

        result.Status.Should().Be(expected);
        result.Reason.Should().Be("because");
        result.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-05-21T10:30:00Z"));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.AbsolutePath.Should()
            .Be("/api/v1/approvals/requests/approval-1/status");
    }

    private static NyxIdRemoteToolApprovalPort CreatePort(CaptureHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        return new NyxIdRemoteToolApprovalPort(client, NullLogger<NyxIdRemoteToolApprovalPort>.Instance);
    }

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
