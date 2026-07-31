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
    private const string SuccessfulMutationResult = "{\"ok\":true}";

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
                AgentToolExecutionOwners.Actor("actor-nyx"),
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
            executed.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
            executed.Receipt.ErrorCode.Should().Be("invalid_action");
            executed.Receipt.ResultJson.Should().Be(executed.ResultJson);
            executed.TerminalInvoked.Should().BeTrue();
            http.Requests.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ServicesTool_WhenInvalidActionContainsCredential_ShouldNotExposeCredential()
    {
        const string argumentsJson =
            "{\"action\":\"unknown\",\"credential\":\"bearer-secret\"}";
        var tool = new NyxIdServicesTool(CreateClient(new RecordingHttpHandler()));
        var executor = CreateExecutor();
        var waiting = await executor.ExecuteAsync(Request(
            tool,
            argumentsJson,
            AgentToolApprovalContinuationMode.ActorOwned));
        var grant = new AgentToolApprovalGrant(
            AgentToolExecutionOwners.Actor("actor-nyx"),
            waiting.Receipt.ApprovalRequestId,
            "request-nyx",
            tool.Name,
            "call-nyx",
            AgentToolArgumentsDigest.ComputeSha256(argumentsJson));

        var outcome = await executor.ExecuteAsync(Request(
            tool,
            argumentsJson,
            AgentToolApprovalContinuationMode.ActorOwned,
            grant));

        outcome.ResultJson.Should().Be("{\"error\":\"invalid_action\"}");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ErrorCode.Should().Be("invalid_action");
        outcome.ToString().Should().NotContain("bearer-secret");
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

    public static TheoryData<string, string, string, string, string, string?> MutationCases => new()
    {
        {
            "approvals",
            "enable",
            "{\"action\":\"enable\"}",
            "PUT",
            "/api/v1/notifications/settings",
            "{\"approval_required\":true}"
        },
        {
            "approvals",
            "approve",
            "{\"action\":\"approve\",\"id\":\"approval-1\"}",
            "POST",
            "/api/v1/approvals/requests/approval-1/decide",
            "{\"approved\":true}"
        },
        {
            "approvals",
            "reject",
            "{\"action\":\"reject\",\"id\":\"approval-1\"}",
            "POST",
            "/api/v1/approvals/requests/approval-1/decide",
            "{\"approved\":false}"
        },
        {
            "approvals",
            "deny",
            "{\"action\":\"deny\",\"id\":\"approval-1\"}",
            "POST",
            "/api/v1/approvals/requests/approval-1/decide",
            "{\"approved\":false}"
        },
        {
            "approvals",
            "revoke_grant",
            "{\"action\":\"revoke_grant\",\"id\":\"grant-1\"}",
            "DELETE",
            "/api/v1/approvals/grants/grant-1",
            null
        },
        {
            "approvals",
            "disable",
            "{\"action\":\"disable\"}",
            "PUT",
            "/api/v1/notifications/settings",
            "{\"approval_required\":false}"
        },
        {
            "approvals",
            "set_config",
            "{\"action\":\"set_config\",\"id\":\"config-1\",\"require_approval\":true,\"approval_mode\":\"grant\"}",
            "PUT",
            "/api/v1/approvals/service-configs/config-1",
            "{\"require_approval\":true,\"approval_mode\":\"grant\"}"
        },
        {
            "services",
            "create",
            "{\"action\":\"create\",\"service_slug\":\"mail\",\"credential\":\"create-secret\"}",
            "POST",
            "/api/v1/keys",
            "{\"service_slug\":\"mail\",\"credential\":\"create-secret\",\"label\":\"mail\"}"
        },
        {
            "services",
            "update",
            "{\"action\":\"update\",\"id\":\"service-1\",\"label\":\"Mail\",\"endpoint_url\":\"https://mail.example.com\",\"node_id\":\"node-1\",\"active\":true}",
            "PUT",
            "/api/v1/keys/service-1",
            "{\"label\":\"Mail\",\"endpoint_url\":\"https://mail.example.com\",\"node_id\":\"node-1\",\"is_active\":true}"
        },
        {
            "services",
            "route",
            "{\"action\":\"route\",\"id\":\"service-1\",\"direct\":true}",
            "PUT",
            "/api/v1/keys/service-1",
            "{\"node_id\":\"\"}"
        },
        {
            "services",
            "delete",
            "{\"action\":\"delete\",\"id\":\"service-1\"}",
            "DELETE",
            "/api/v1/keys/service-1",
            null
        },
    };

    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task MutationWithMatchingGrant_ShouldExecuteHttpAndRecordReceiptAndAudits(
        string toolKind,
        string action,
        string argumentsJson,
        string expectedMethod,
        string expectedPath,
        string? expectedBody)
    {
        _ = action;
        var http = new RecordingHttpHandler(SuccessfulMutationResult);
        IAgentTool tool = toolKind == "approvals"
            ? new NyxIdApprovalsTool(CreateClient(http))
            : new NyxIdServicesTool(CreateClient(http));
        var audit = new RecordingAuditTrail();
        var outcome = await ExecuteWithMatchingGrantAsync(
            CreateExecutor(audit),
            tool,
            argumentsJson);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.ResultJson.Should().Be(SuccessfulMutationResult);
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.ResultJson.Should().Be(SuccessfulMutationResult);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.AuditCompleted.Should().BeTrue();
        var request = http.Requests.Should().ContainSingle().Subject;
        request.Method.Method.Should().Be(expectedMethod);
        request.RequestUri!.AbsolutePath.Should().Be(expectedPath);
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be("token-1");
        http.Bodies.Should().ContainSingle().Which.Should().Be(expectedBody);
        AssertCompletedMutationAudit(
            audit,
            tool.Name,
            argumentsJson,
            AgentToolReceiptStatus.Success,
            AuditOutcome.Success,
            AuditTerminalOutcome.Succeeded);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"id\":\"service-1\"}")]
    public async Task RotateCredential_WhenServiceLookupIsUnusable_ShouldReturnSafeAuditedError(
        string serviceResponse)
    {
        const string argumentsJson =
            "{\"action\":\"rotate_credential\",\"id\":\"service-1\",\"credential\":\"rotate-secret\"}";
        var http = new RecordingHttpHandler(serviceResponse);
        var tool = new NyxIdServicesTool(CreateClient(http));
        var audit = new RecordingAuditTrail();

        var outcome = await ExecuteWithMatchingGrantAsync(
            CreateExecutor(audit),
            tool,
            argumentsJson);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.ResultJson.Should().Be(
            "{\"error\":\"invalid_arguments\",\"message\":\"The NyxID tool arguments are invalid.\"}");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ErrorCode.Should().Be("invalid_arguments");
        outcome.Receipt.ResultJson.Should().Be(outcome.ResultJson);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.AuditCompleted.Should().BeTrue();
        var request = http.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsolutePath.Should().Be("/api/v1/keys/service-1");
        http.Bodies.Should().ContainSingle().Which.Should().BeNull();
        outcome.ToString().Should().NotContain("rotate-secret");
        audit.Records.Should().NotContain(record => record.ToString().Contains("rotate-secret"));
        AssertCompletedMutationAudit(
            audit,
            tool.Name,
            argumentsJson,
            AgentToolReceiptStatus.Error,
            AuditOutcome.Error,
            AuditTerminalOutcome.Failed);
    }

    [Fact]
    public async Task RotateCredential_WithMatchingGrant_ShouldLookupServiceThenUpdateExternalKey()
    {
        const string argumentsJson =
            "{\"action\":\"rotate_credential\",\"id\":\"service-1\",\"credential\":\"rotate-secret\"}";
        var http = new RecordingHttpHandler(
            "{\"id\":\"service-1\",\"api_key_id\":\"key-9\"}",
            "{\"rotated\":true}");
        var tool = new NyxIdServicesTool(CreateClient(http));
        var audit = new RecordingAuditTrail();

        var outcome = await ExecuteWithMatchingGrantAsync(
            CreateExecutor(audit),
            tool,
            argumentsJson);

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.ResultJson.Should().Be("{\"rotated\":true}");
        outcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.ResultJson.Should().Be(outcome.ResultJson);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.AuditCompleted.Should().BeTrue();
        http.Requests.Select(request => (request.Method, request.RequestUri!.AbsolutePath)).Should().Equal(
            (HttpMethod.Get, "/api/v1/keys/service-1"),
            (HttpMethod.Put, "/api/v1/api-keys/external/key-9"));
        http.Bodies.Should().Equal(null, "{\"credential\":\"rotate-secret\"}");
        AssertCompletedMutationAudit(
            audit,
            tool.Name,
            argumentsJson,
            AgentToolReceiptStatus.Success,
            AuditOutcome.Success,
            AuditTerminalOutcome.Succeeded);
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

    private static async Task<AgentToolExecutionOutcome> ExecuteWithMatchingGrantAsync(
        AdmittedAgentToolExecutor executor,
        IAgentTool tool,
        string argumentsJson)
    {
        var waiting = await executor.ExecuteAsync(Request(
            tool,
            argumentsJson,
            AgentToolApprovalContinuationMode.ActorOwned));
        waiting.Kind.Should().Be(AgentToolExecutionOutcomeKind.ApprovalRequired);
        waiting.Receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        waiting.Receipt.ApprovalRequestId.Should().NotBeNullOrWhiteSpace();
        waiting.TerminalInvoked.Should().BeFalse();
        waiting.AuditCompleted.Should().BeTrue();
        var grant = new AgentToolApprovalGrant(
            AgentToolExecutionOwners.Actor("actor-nyx"),
            waiting.Receipt.ApprovalRequestId,
            "request-nyx",
            tool.Name,
            "call-nyx",
            AgentToolArgumentsDigest.ComputeSha256(argumentsJson));

        return await executor.ExecuteAsync(Request(
            tool,
            argumentsJson,
            AgentToolApprovalContinuationMode.ActorOwned,
            grant));
    }

    private static void AssertCompletedMutationAudit(
        RecordingAuditTrail audit,
        string toolName,
        string argumentsJson,
        AgentToolReceiptStatus terminalReceiptStatus,
        AuditOutcome terminalOutcome,
        AuditTerminalOutcome terminalTerminalOutcome)
    {
        audit.Records.Select(record => record.ToolExecution.ExecutionPhase).Should().Equal(
            AuditToolExecutionPhase.WaitingApproval,
            AuditToolExecutionPhase.Running,
            AuditToolExecutionPhase.Terminal);
        audit.Records.Select(record => record.AuditId).Should().OnlyHaveUniqueItems();
        var digest = AgentToolArgumentsDigest.ComputeSha256(argumentsJson);
        var running = audit.Records[1];
        var terminal = audit.Records[2];
        foreach (var record in new[] { running, terminal })
        {
            record.OperationName.Should().Be(toolName);
            record.Correlation.RequestId.Should().Be("request-nyx");
            record.Correlation.CallId.Should().Be("call-nyx");
            record.ToolExecution.ArgumentsSha256.Should().Be(digest);
            record.ToolExecution.IsMutation.Should().BeTrue();
        }

        running.LifecyclePhase.Should().Be(AuditLifecyclePhase.Running);
        running.Outcome.Should().Be(AuditOutcome.Accepted);
        running.Annotations["tool_receipt_status"].Should().Be(AgentToolReceiptStatus.Unspecified.ToString());
        terminal.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        terminal.Outcome.Should().Be(terminalOutcome);
        terminal.TerminalOutcome.Should().Be(terminalTerminalOutcome);
        terminal.Annotations["tool_receipt_status"].Should().Be(terminalReceiptStatus.ToString());
    }

    private static AdmittedAgentToolExecutor CreateExecutor(IAuditTrailAppender? auditTrail = null) =>
        new(
            AlwaysStartingAgentToolAdmissionLedger.Instance,
            auditTrail ?? new AppendedAuditTrail(),
            new StableIdentityHasher());

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
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-nyx"),
        };

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

    private sealed class RecordingHttpHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> _responseBodies = new(responseBodies);

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
            var responseBody = _responseBodies.TryDequeue(out var response)
                ? response
                : "[]";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            };
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

    private sealed class RecordingAuditTrail : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
}
