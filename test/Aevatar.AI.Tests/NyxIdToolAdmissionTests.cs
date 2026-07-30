using System.Net;
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

public sealed class NyxIdToolAdmissionTests
{
    public static TheoryData<string> InvalidActions => new()
    {
        "",
        "   ",
        "not-json",
        "{",
        "[]",
        "\"list\"",
        "{\"action\":42}",
        "{\"action\":null}",
        "{\"action\":\"\"}",
        "{\"action\":\"   \"}",
        "{\"action\":\"unknown\"}",
    };

    [Theory]
    [MemberData(nameof(InvalidActions))]
    public async Task AggregateTools_WhenActionIsInvalid_ShouldGateThenReturnInvalidActionWithoutHttp(
        string argumentsJson)
    {
        foreach (var toolKind in new[] { "approvals", "services" })
        {
            var http = new RecordingHttpHandler();
            var client = CreateClient(http);
            IAgentTool tool = toolKind == "approvals"
                ? new NyxIdApprovalsTool(client)
                : new NyxIdServicesTool(client);
            var executor = CreateExecutor();

            var waiting = await executor.ExecuteAsync(Request(
                tool,
                argumentsJson,
                AgentToolApprovalContinuationMode.ActorOwned));

            waiting.Kind.Should().Be(AgentToolExecutionOutcomeKind.ApprovalRequired);
            waiting.IsMutation.Should().BeTrue();
            waiting.Receipt.IsDestructive.Should().BeTrue();
            waiting.TerminalInvoked.Should().BeFalse();
            http.Requests.Should().BeEmpty();

            var grant = new AgentToolApprovalGrant(
                waiting.Receipt.ApprovalRequestId,
                "request-nyx",
                tool.Name,
                "call-nyx",
                AgentToolArgumentsDigest.ComputeSha256(argumentsJson));
            var executed = await executor.ExecuteAsync(Request(
                tool,
                argumentsJson,
                AgentToolApprovalContinuationMode.ActorOwned,
                grant));

            executed.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
            executed.ResultJson.Should().Contain("invalid_action");
            executed.TerminalInvoked.Should().BeTrue();
            http.Requests.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("list", false, true, false)]
    [InlineData("show", false, true, false)]
    [InlineData("configs", false, true, false)]
    [InlineData("grants", false, true, false)]
    [InlineData("enable", true, false, false)]
    [InlineData("approve", true, false, true)]
    [InlineData("reject", true, false, true)]
    [InlineData("deny", true, false, true)]
    [InlineData("revoke_grant", true, false, true)]
    [InlineData("disable", true, false, true)]
    [InlineData("set_config", true, false, true)]
    public void ApprovalsTool_ShouldUseClosedActionMatrix(
        string action,
        bool requiresApproval,
        bool isReadOnly,
        bool isDestructive)
    {
        var tool = new NyxIdApprovalsTool(CreateClient(new RecordingHttpHandler()));

        tool.GetCallSafety($$"""{"action":"{{action}}"}""")
            .Should().Be(new AgentToolCallSafety(requiresApproval, isReadOnly, isDestructive));
    }

    [Theory]
    [InlineData("list", false, true, false)]
    [InlineData("show", false, true, false)]
    [InlineData("create", true, false, false)]
    [InlineData("update", true, false, false)]
    [InlineData("route", true, false, false)]
    [InlineData("delete", true, false, true)]
    [InlineData("rotate_credential", true, false, true)]
    public void ServicesTool_ShouldUseClosedActionMatrix(
        string action,
        bool requiresApproval,
        bool isReadOnly,
        bool isDestructive)
    {
        var tool = new NyxIdServicesTool(CreateClient(new RecordingHttpHandler()));

        tool.GetCallSafety($$"""{"action":"{{action}}"}""")
            .Should().Be(new AgentToolCallSafety(requiresApproval, isReadOnly, isDestructive));
    }

    [Theory]
    [InlineData("approvals", "enable", "{\"action\":\"enable\"}")]
    [InlineData("approvals", "approve", "{\"action\":\"approve\",\"id\":\"approval-1\"}")]
    [InlineData("approvals", "reject", "{\"action\":\"reject\",\"id\":\"approval-1\"}")]
    [InlineData("approvals", "deny", "{\"action\":\"deny\",\"id\":\"approval-1\"}")]
    [InlineData("approvals", "revoke_grant", "{\"action\":\"revoke_grant\",\"id\":\"grant-1\"}")]
    [InlineData("approvals", "disable", "{\"action\":\"disable\"}")]
    [InlineData("approvals", "set_config", "{\"action\":\"set_config\",\"id\":\"config-1\"}")]
    [InlineData("services", "create", "{\"action\":\"create\",\"service_slug\":\"mail\",\"credential\":\"secret\"}")]
    [InlineData("services", "update", "{\"action\":\"update\",\"id\":\"service-1\",\"label\":\"Mail\"}")]
    [InlineData("services", "route", "{\"action\":\"route\",\"id\":\"service-1\",\"direct\":true}")]
    [InlineData("services", "delete", "{\"action\":\"delete\",\"id\":\"service-1\"}")]
    [InlineData("services", "rotate_credential", "{\"action\":\"rotate_credential\",\"id\":\"service-1\",\"credential\":\"secret\"}")]
    public async Task MutationWithoutDurableGrant_ShouldBeDeniedBeforeHttp(
        string toolKind,
        string action,
        string argumentsJson)
    {
        var http = new RecordingHttpHandler();
        var client = CreateClient(http);
        IAgentTool tool = toolKind == "approvals"
            ? new NyxIdApprovalsTool(client)
            : new NyxIdServicesTool(client);
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync(Request(tool, argumentsJson));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.IsMutation.Should().BeTrue();
        outcome.FailureCode.Should().Be("approval_required_without_actor_continuation");
        outcome.TerminalInvoked.Should().BeFalse();
        http.Requests.Should().BeEmpty();
        if (action == "rotate_credential")
        {
            http.Requests.Count(static request => request.Method == HttpMethod.Get).Should().Be(0);
            http.Requests.Count(static request => request.Method == HttpMethod.Put).Should().Be(0);
        }
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("codex")]
    public async Task SshToolsDeniedByAdmission_ShouldMakeNoSshRequest(string kind)
    {
        var ssh = new RecordingSshExecutor();
        IAgentTool tool = kind == "ssh"
            ? new NyxIdSshExecTool(ssh, new NyxIdToolOptions())
            : new NyxIdCodexExecTool(ssh, new NyxIdToolOptions());
        var argumentsJson = kind == "ssh"
            ? "{\"service\":\"svc\",\"principal\":\"root\",\"command\":\"uptime\"}"
            : "{\"service\":\"svc\",\"principal\":\"root\",\"prompt\":\"inspect\"}";

        var outcome = await CreateExecutor().ExecuteAsync(Request(tool, argumentsJson));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("approval_required_without_actor_continuation");
        outcome.TerminalInvoked.Should().BeFalse();
        ssh.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("approvals", "/api/v1/approvals/requests")]
    [InlineData("services", "/api/v1/keys")]
    public async Task AggregateToolObjectWithoutAction_ShouldDefaultToReadOnlyList(
        string toolKind,
        string expectedPath)
    {
        var http = new RecordingHttpHandler();
        var client = CreateClient(http);
        IAgentTool tool = toolKind == "approvals"
            ? new NyxIdApprovalsTool(client)
            : new NyxIdServicesTool(client);

        var outcome = await CreateExecutor().ExecuteAsync(Request(tool, "{}"));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.IsMutation.Should().BeFalse();
        outcome.TerminalInvoked.Should().BeTrue();
        var request = http.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsolutePath.Should().Be(expectedPath);
    }

    private static AdmittedAgentToolExecutor CreateExecutor() =>
        new(new AppendedAuditTrail(), new StableIdentityHasher());

    private static AgentToolExecutionRequest Request(
        IAgentTool tool,
        string argumentsJson,
        AgentToolApprovalContinuationMode approvalContinuationMode = AgentToolApprovalContinuationMode.None,
        AgentToolApprovalGrant? approvalGrant = null) =>
        new(
            tool,
            argumentsJson,
            ExecutionContext(),
            approvalContinuationMode,
            approvalGrant);

    private static AgentToolExecutionContext ExecutionContext() =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-nyx", "call-nyx"),
            Credentials = AgentToolCredentials.Empty with { NyxIdAccessToken = "token-1" },
        };

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]"),
            });
        }
    }

    private sealed class RecordingSshExecutor : INyxIdSshCommandExecutor
    {
        public int Calls { get; private set; }

        public Task<string> ExecuteAsync(NyxIdSshCommandRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("{}");
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
