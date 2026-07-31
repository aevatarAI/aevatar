using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApprovalsToolTests
{
    [Theory]
    [InlineData("list", "/api/v1/approvals/requests")]
    [InlineData("show", "/api/v1/approvals/requests/approval-1")]
    public async Task ExecuteAsync_ListAndShow_ShouldExposeOnlyAllowlistedApprovalFields(
        string action,
        string expectedPath)
    {
        const string credential = "credential-secret-sentinel";
        const string providerInput = "raw-provider-input-sentinel";
        const string invalidAction = "invalid-action-input-sentinel";
        var persistedApproval = $$"""
            {
              "id": "approval-1",
              "service_name": "nyxid_services",
              "service_slug": "tool",
              "requester_type": "delegated",
              "requester_label": "automation-alpha",
              "operation_summary": "tool:nyxid_services",
              "action_description": "nyxid_services({\"action\":\"unknown\",\"credential\":\"{{credential}}\"})",
              "http_method": "POST",
              "resource": "{{providerInput}}",
              "verb": "destructive",
              "tool_name": "nyxid_services",
              "tool_call_id": "call-approval-1",
              "tool_arguments": "{\"action\":\"{{invalidAction}}\",\"credential\":\"{{credential}}\",\"provider_input\":\"{{providerInput}}\"}",
              "is_destructive": true,
              "approval_mode": "per_request",
              "status": "pending",
              "created_at": "2026-07-31T01:02:03Z",
              "decided_at": null,
              "decision_channel": null,
              "from_org_policy": false,
              "org_id": null,
              "org_name": null
            }
            """;
        var responseJson = action == "list"
            ? $$"""{"requests":[{{persistedApproval}}],"total":1,"page":1,"per_page":20,"provider_debug":"{{providerInput}}"}"""
            : persistedApproval;
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
        });
        var tool = new NyxIdApprovalsTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance));
        var executor = new AdmittedAgentToolExecutor(
            new AppendedAuditTrail(),
            new StableIdentityHasher());
        var argumentsJson = action == "list"
            ? "{\"action\":\"list\"}"
            : "{\"action\":\"show\",\"id\":\"approval-1\"}";

        var outcome = await executor.ExecuteAsync(new AgentToolExecutionRequest(
            tool,
            argumentsJson,
            AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with { NyxIdAccessToken = "token-1" },
                Request = new AgentToolRequestIdentity("request-1", "call-1"),
            },
            AgentToolApprovalContinuationMode.None,
            null));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.ResultJson.Should().Be(outcome.ResultJson);
        outcome.ResultJson.Should()
            .NotContain(credential)
            .And.NotContain(providerInput)
            .And.NotContain(invalidAction)
            .And.NotContain("tool_arguments")
            .And.NotContain("action_description")
            .And.NotContain("requester_label");
        using var result = JsonDocument.Parse(outcome.ResultJson);
        var approval = action == "list"
            ? result.RootElement.GetProperty("requests")[0]
            : result.RootElement;
        approval.GetProperty("id").GetString().Should().Be("approval-1");
        approval.GetProperty("service_name").GetString().Should().Be("nyxid_services");
        approval.GetProperty("service_slug").GetString().Should().Be("tool");
        approval.GetProperty("tool_name").GetString().Should().Be("nyxid_services");
        approval.GetProperty("tool_call_id").GetString().Should().Be("call-approval-1");
        approval.GetProperty("is_destructive").GetBoolean().Should().BeTrue();
        approval.GetProperty("approval_mode").GetString().Should().Be("per_request");
        approval.GetProperty("status").GetString().Should().Be("pending");
        approval.GetProperty("created_at").GetString().Should().Be("2026-07-31T01:02:03Z");
        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsolutePath.Should().Be(expectedPath);
    }

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
            Credentials = AgentToolCredentials.Empty with { NyxIdAccessToken = token },
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

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
}
