using System.Net;
using System.Text;
using System.Text.Json;
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

        using var _ = AgentToolContextScope.Push(WithNyxIdAccessToken("token-1"));
        var result = await port.SubmitAsync(
            new RemoteToolApprovalRequest(
                "req-1",
                "ssh_exec",
                "call-1",
                """{"command":"uptime"}""",
                ToolApprovalMode.Auto,
                true),
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
    [InlineData("cancelled", RemoteToolApprovalStatus.Cancelled)]
    [InlineData("canceled", RemoteToolApprovalStatus.Cancelled)]
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

        using var _ = AgentToolContextScope.Push(WithNyxIdAccessToken("token-1"));
        var result = await port.GetStatusAsync(
            new RemoteToolApprovalStatusQuery(
                "req-1",
                "approval-1"),
            CancellationToken.None);

        result.Status.Should().Be(expected);
        result.Reason.Should().Be("because");
        result.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-05-21T10:30:00Z"));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.AbsolutePath.Should()
            .Be("/api/v1/approvals/requests/approval-1/status");
        handler.Requests[0].Headers.Authorization!.ToString().Should().Be("Bearer token-1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DecideAsync_ShouldCallDecisionEndpointWithApprovedBoolBody(bool approved)
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"approval-1","status":"decided"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var port = CreatePort(handler);

        using var _ = AgentToolContextScope.Push(WithNyxIdAccessToken("token-1"));
        var result = await port.DecideAsync(
            new RemoteToolApprovalDecision("approval-1", approved),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should()
            .Be("/api/v1/approvals/requests/approval-1/decide");
        handler.Requests[0].Headers.Authorization!.ToString().Should().Be("Bearer token-1");
        handler.Bodies[0].Should().Be(approved ? """{"approved":true}""" : """{"approved":false}""");
        handler.Bodies[0].Should().NotContain("decision");
    }

    [Fact]
    public async Task DecideAsync_ShouldMapNyxIdErrorEnvelope()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"error":"already_decided","message":"Approval already decided"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var port = CreatePort(handler);

        using var _ = AgentToolContextScope.Push(WithNyxIdAccessToken("token-1"));
        var result = await port.DecideAsync(
            new RemoteToolApprovalDecision("approval-1", false),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Status.Should().Be(409);
        result.ErrorKey.Should().Be("already_decided");
        result.Detail.Should().Be("Approval already decided");
    }

    [Fact]
    public async Task SubmitAsync_ShouldRequireTypedCredential()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"approval-1"}""", Encoding.UTF8, "application/json"),
        });
        var port = CreatePort(handler);

        using var _ = AgentToolContextScope.Push(null);
        var act = () => port.SubmitAsync(
            new RemoteToolApprovalRequest(
                "req-1",
                "ssh_exec",
                "call-1",
                "{}",
                ToolApprovalMode.Auto,
                false),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("NyxID authentication required for remote approval.");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatusAsync_ShouldRequireTypedCredential()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"pending"}""", Encoding.UTF8, "application/json"),
        });
        var port = CreatePort(handler);

        using var _ = AgentToolContextScope.Push(null);
        var act = () => port.GetStatusAsync(
            new RemoteToolApprovalStatusQuery("req-1", "approval-1"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("NyxID authentication required for remote approval.");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DecideAsync_ShouldRequireTypedCredential()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"approval-1"}""", Encoding.UTF8, "application/json"),
        });
        var port = CreatePort(handler);

        using var _ = AgentToolContextScope.Push(null);
        var act = () => port.DecideAsync(
            new RemoteToolApprovalDecision("approval-1", true),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("NyxID authentication required for remote approval.");
        handler.Requests.Should().BeEmpty();
    }

    private static AgentToolExecutionContext WithNyxIdAccessToken(string token) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with { NyxIdAccessToken = token },
        };

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
