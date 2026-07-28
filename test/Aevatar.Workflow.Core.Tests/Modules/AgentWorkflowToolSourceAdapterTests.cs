using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class AgentWorkflowToolSourceAdapterTests
{
    [Fact]
    public async Task WorkflowTool_ShouldMapWorkflowRequestToAgentToolExecutionContext()
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter([new SingleAgentToolSource(agentTool)]);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"ok":true}""",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential { BearerToken = "token-123" },
                RuntimeContext: WorkflowToolRuntimeContext.Empty,
                IdempotencyKey: "idem-agent-tool-1",
                ScheduleId: " schedule-1 "),
            CancellationToken.None);

        result.ResultJson.Should().Be("""{"observed":true}""");
        result.ManagedHandoff.Should().BeNull();
        agentTool.ObservedArgumentsJson.Should().Be("""{"ok":true}""");
        agentTool.ObservedAccessToken.Should().Be("token-123");
        agentTool.ObservedOrgToken.Should().Be("token-123");
        agentTool.ObservedScopeId.Should().Be("scope-1");
        agentTool.ObservedCallId.Should().Be("call-1");
        agentTool.ObservedIdempotencyKey.Should().Be("idem-agent-tool-1");
        agentTool.ObservedScheduleId.Should().Be("schedule-1");
        agentTool.ObservedExternalMetadata.Should().NotContainKey("ExecutionId");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_ShouldExecuteAgentToolThroughToolMiddlewareChain()
    {
        var agentTool = new CapturingAgentTool();
        var middleware = new RewritingToolCallMiddleware();
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            [middleware]);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"original":true}""",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        agentTool.ObservedArgumentsJson.Should().Be("""{"rewritten":true}""");
        result.ResultJson.Should().Be("""{"middleware":true}""");
        middleware.NextExecuted.Should().BeTrue();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_WhenApprovalDenied_ShouldFailClosedWithoutExecutingAgentTool()
    {
        var agentTool = new CapturingAgentTool(ToolApprovalMode.AlwaysRequire);
        var approvalHandler = new ScriptedApprovalHandler(ToolApprovalResult.Denied("blocked"));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            approvalHandler: approvalHandler);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await FluentActions.Awaiting(() => tool.ExecuteAsync(
                new WorkflowToolExecutionRequest(
                    ArgumentsJson: "{}",
                    RunId: "run-1",
                    StepId: "step-1",
                    ExecutionId: "exec-1",
                    CallId: "call-1",
                    ScopeId: "scope-1",
                    CallerCredential: new WorkflowCallerCredential()),
                CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*ApprovalDenied*blocked*");

        agentTool.ExecuteCount.Should().Be(0);
        approvalHandler.Requests.Should().ContainSingle();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_WhenApprovalPending_ShouldReturnTypedPendingOutcomeWithoutExecutingAgentTool()
    {
        var agentTool = new CapturingAgentTool(ToolApprovalMode.AlwaysRequire);
        var approvalHandler = new ScriptedApprovalHandler(ToolApprovalResult.Yielded("approval-1"));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            approvalHandler: approvalHandler);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: """{"danger":true}""",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        agentTool.ExecuteCount.Should().Be(0);
        approvalHandler.Requests.Should().ContainSingle();
        result.PendingApproval.Should().NotBeNull();
        result.PendingApproval!.ApprovalRequestId.Should().Be(approvalHandler.Requests.Single().RequestId);
        result.PendingApproval.ApprovalRequestId.Should().NotBeNullOrWhiteSpace();
        result.PendingApproval.ToolName.Should().Be("capture_context");
        result.PendingApproval.ToolCallId.Should().Be("call-1");
        result.PendingApproval.ArgumentsJson.Should().Be("""{"danger":true}""");
        result.ResultJson.Should().BeEmpty();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_ShouldYieldProofBoundWriteUntilMatchingApprovalGrant()
    {
        var agentTool = new ProofPolicyAgentTool();
        var approvalHandler = new ScriptedApprovalHandler(ToolApprovalResult.Yielded("approval-write"));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            approvalHandler: approvalHandler);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();
        var admission = WriteInvocationAdmission();
        var request = new WorkflowToolExecutionRequest(
            ArgumentsJson: "{}",
            RunId: "run-alpha",
            StepId: "write-alpha",
            ExecutionId: "exec-alpha",
            CallId: "call-alpha",
            ScopeId: "scope-alpha",
            CallerCredential: new WorkflowCallerCredential(),
            RuntimeContext: new WorkflowToolRuntimeContext(
                "workflow-run-actor-alpha",
                "run-alpha",
                "write-alpha",
                "run-alpha",
                1),
            InvocationAdmission: admission);

        var pending = await tool.ExecuteAsync(request, CancellationToken.None);

        pending.PendingApproval.Should().NotBeNull();
        agentTool.ExecuteCount.Should().Be(0);
        approvalHandler.Requests.Should().ContainSingle().Which.IsDestructive.Should().BeFalse();

        var resumed = await tool.ExecuteAsync(request with
        {
            ApprovalGrant = new Aevatar.Workflow.Core.Modules.ToolApprovalGrant(
                pending.PendingApproval!.ApprovalRequestId,
                agentTool.Name,
                "call-alpha"),
        }, CancellationToken.None);

        resumed.ResultJson.Should().Be("""{"executed":true}""");
        agentTool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task WorkflowTool_ShouldUseSingleCanonicalApprovalMiddlewareWhenHostRegistersDuplicate()
    {
        var agentTool = new CapturingAgentTool(ToolApprovalMode.AlwaysRequire);
        var canonicalHandler = new ScriptedApprovalHandler(ToolApprovalResult.Approved());
        var duplicateHandler = new ScriptedApprovalHandler(ToolApprovalResult.Denied("duplicate"));
        var adapter = new AgentWorkflowToolSourceAdapter(
            [new SingleAgentToolSource(agentTool)],
            [new ToolApprovalMiddleware(duplicateHandler)],
            canonicalHandler);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.ResultJson.Should().Be("""{"observed":true}""");
        agentTool.ExecuteCount.Should().Be(1);
        canonicalHandler.Requests.Should().ContainSingle();
        duplicateHandler.Requests.Should().BeEmpty();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_ShouldMapRuntimeContextToAgentToolExecutionContext()
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter([new SingleAgentToolSource(agentTool)]);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential { BearerToken = "token-123" },
                RuntimeContext: new WorkflowToolRuntimeContext(
                    " parent-actor ",
                    " parent-run ",
                    " parent-step ",
                    " root-run ",
                    2)),
            CancellationToken.None);

        agentTool.ObservedWorkflowRuntime.ParentActorId.Should().Be("parent-actor");
        agentTool.ObservedWorkflowRuntime.ParentRunId.Should().Be("parent-run");
        agentTool.ObservedWorkflowRuntime.ParentStepId.Should().Be("parent-step");
        agentTool.ObservedWorkflowRuntime.RootRunId.Should().Be("root-run");
        agentTool.ObservedWorkflowRuntime.Depth.Should().Be(2);
        agentTool.ObservedWorkflowRuntime.HasManagedParent.Should().BeTrue();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("", "", null, null)]
    [InlineData("  ", "\t", null, null)]
    [InlineData(" call-1 ", " scope-1 ", "call-1", "scope-1")]
    public async Task WorkflowTool_ShouldNormalizeWorkflowCallAndScopeIdsForAgentToolContext(
        string? callId,
        string? scopeId,
        string? expectedCallId,
        string? expectedScopeId)
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter([new SingleAgentToolSource(agentTool)]);
        var tool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await tool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: callId!,
                ScopeId: scopeId!,
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        agentTool.ObservedCallId.Should().Be(expectedCallId);
        agentTool.ObservedScopeId.Should().Be(expectedScopeId);
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WorkflowTool_ShouldPreserveRuntimeContextWhenWorkflowCredentialIsMissing(string authorization)
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter([new SingleAgentToolSource(agentTool)]);
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await workflowTool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential { BearerToken = authorization },
                RuntimeContext: new WorkflowToolRuntimeContext(
                    " parent-actor ",
                    " parent-run ",
                    " parent-step ",
                    " root-run ",
                    -1)),
            CancellationToken.None);

        agentTool.ObservedAccessToken.Should().BeNull();
        agentTool.ObservedOrgToken.Should().BeNull();
        agentTool.ObservedWorkflowRuntime.ParentActorId.Should().Be("parent-actor");
        agentTool.ObservedWorkflowRuntime.ParentRunId.Should().Be("parent-run");
        agentTool.ObservedWorkflowRuntime.ParentStepId.Should().Be("parent-step");
        agentTool.ObservedWorkflowRuntime.RootRunId.Should().Be("root-run");
        agentTool.ObservedWorkflowRuntime.Depth.Should().Be(0);
        agentTool.ObservedWorkflowRuntime.HasManagedParent.Should().BeTrue();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Theory]
    [InlineData("Basic token-123")]
    [InlineData("Bearer token-123")]
    [InlineData("Bearer ")]
    [InlineData("token 123")]
    public async Task WorkflowTool_ShouldRejectMalformedWorkflowCredential(string authorization)
    {
        var agentTool = new CapturingAgentTool();
        var adapter = new AgentWorkflowToolSourceAdapter([new SingleAgentToolSource(agentTool)]);
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        await FluentActions.Awaiting(() => workflowTool.ExecuteAsync(
                new WorkflowToolExecutionRequest(
                    ArgumentsJson: "{}",
                    RunId: "run-1",
                    StepId: "step-1",
                    ExecutionId: "exec-1",
                    CallId: "call-1",
                    ScopeId: "scope-1",
                    CallerCredential: new WorkflowCallerCredential { BearerToken = authorization }),
                CancellationToken.None))
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*caller credential*invalid*");

        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowTool_WhenProviderReceiptIsError_ShouldReturnTypedFailure()
    {
        const string rawResult = """{"error":true,"status":503}""";
        const string safeResult = """{"error":"PROVIDER_HTTP_503","message":"The service request failed."}""";
        var agentTool = new ResultReceiptAgentTool(
            rawResult,
            new AgentToolReceipt
            {
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "PROVIDER_HTTP_503",
                ErrorMessage = "The service request failed.",
                ResultJson = safeResult,
            });
        var adapter = new AgentWorkflowToolSourceAdapter([new SingleAgentToolSource(agentTool)]);
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await workflowTool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.ResultJson.Should().Be(safeResult);
        result.Failure.Should().NotBeNull();
        result.Failure!.ErrorCode.Should().Be("PROVIDER_HTTP_503");
        result.Failure.ErrorMessage.Should().Be("The service request failed.");
    }

    [Fact]
    public async Task WorkflowTool_WhenUnclassifiedResultContainsErrorField_ShouldRemainSuccessful()
    {
        const string resultJson = """{"error":true,"status":503,"historical":true}""";
        var agentTool = new ResultReceiptAgentTool(resultJson, receipt: null);
        var adapter = new AgentWorkflowToolSourceAdapter([new SingleAgentToolSource(agentTool)]);
        var workflowTool = (await adapter.GetToolsAsync(CancellationToken.None)).Single();

        var result = await workflowTool.ExecuteAsync(
            new WorkflowToolExecutionRequest(
                ArgumentsJson: "{}",
                RunId: "run-1",
                StepId: "step-1",
                ExecutionId: "exec-1",
                CallId: "call-1",
                ScopeId: "scope-1",
                CallerCredential: new WorkflowCallerCredential()),
            CancellationToken.None);

        result.ResultJson.Should().Be(resultJson);
        result.Failure.Should().BeNull();
    }

    private sealed class CapturingAgentTool(ToolApprovalMode approvalMode = ToolApprovalMode.NeverRequire) : IAgentTool
    {
        public string Name => "capture_context";

        public string Description => "Capture tool context";

        public string ParametersSchema => "{}";

        public ToolApprovalMode ApprovalMode { get; } = approvalMode;

        public int ExecuteCount { get; private set; }

        public string? ObservedArgumentsJson { get; private set; }

        public string? ObservedAccessToken { get; private set; }

        public string? ObservedOrgToken { get; private set; }

        public string? ObservedScopeId { get; private set; }

        public string? ObservedCallId { get; private set; }

        public string? ObservedIdempotencyKey { get; private set; }

        public string? ObservedScheduleId { get; private set; }

        public IReadOnlyDictionary<string, string> ObservedExternalMetadata { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public AgentWorkflowRuntimeContext ObservedWorkflowRuntime { get; private set; } =
            AgentWorkflowRuntimeContext.Empty;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCount++;
            ObservedArgumentsJson = argumentsJson;
            CaptureContext();
            return ExecuteAsyncCore(ct);
        }

        private async Task<string> ExecuteAsyncCore(CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            CaptureContext();
            return """{"observed":true}""";
        }

        private void CaptureContext()
        {
            ObservedAccessToken = AgentToolRequestContext.NyxIdAccessToken;
            ObservedOrgToken = AgentToolRequestContext.NyxIdOrgToken;
            ObservedScopeId = AgentToolRequestContext.ScopeId;
            ObservedCallId = AgentToolRequestContext.CallId;
            ObservedIdempotencyKey = AgentToolRequestContext.IdempotencyKey;
            ObservedScheduleId = AgentToolRequestContext.Current?.Schedule.ScheduleId;
            ObservedExternalMetadata = AgentToolRequestContext.Current?.ExternalMetadata
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            ObservedWorkflowRuntime = AgentToolRequestContext.Current?.WorkflowRuntime
                ?? AgentWorkflowRuntimeContext.Empty;
        }
    }

    private sealed class ProofPolicyAgentTool : IAgentTool
    {
        public string Name => "nyxid_proxy";

        public string Description => "Proof policy fixture";

        public string ParametersSchema => "{}";

        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

        public int ExecuteCount { get; private set; }

        public AgentToolCallSafety GetCallSafety(string argumentsJson)
        {
            var policy = AgentToolRequestContext.Current?.OperationAdmission?.ExecutionPolicy;
            return new AgentToolCallSafety(
                policy?.Approval == AgentToolOperationApproval.Required,
                policy?.Risk == AgentToolOperationRisk.ReadOnly,
                policy?.Risk == AgentToolOperationRisk.Destructive);
        }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("""{"executed":true}""");
        }
    }

    private static ExternalWorkflowCapabilityRef WriteInvocationAdmission() =>
        new()
        {
            NyxIdUserService = new NyxIdUserServiceCapabilityRef
            {
                UserServiceId = "us-write-alpha",
                ServiceSlugSnapshot = "calendar-alpha",
                OperationId = "create-event",
                HttpMethod = "POST",
                PathTemplate = "/events",
                ContractDigest = "digest-write-alpha",
                ExecutionPolicy = new NyxIdOperationExecutionPolicy
                {
                    Risk = NyxIdOperationRisk.Write,
                    Approval = NyxIdOperationApproval.Required,
                    EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                    AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                },
            },
        };

    private sealed class ResultReceiptAgentTool(
        string resultJson,
        AgentToolReceipt? receipt) : IAgentTool
    {
        public string Name => "result_receipt";

        public string Description => "Return a provider-classified result";

        public string ParametersSchema => "{}";

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            receipt?.Clone();

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(resultJson);
        }
    }

    private sealed class RewritingToolCallMiddleware : IToolCallMiddleware
    {
        public bool NextExecuted { get; private set; }

        public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
        {
            context.ArgumentsJson = """{"rewritten":true}""";
            await next();
            NextExecuted = true;
            context.Result = """{"middleware":true}""";
        }
    }

    private sealed class ScriptedApprovalHandler(params ToolApprovalResult[] results) : IToolApprovalHandler
    {
        private readonly Queue<ToolApprovalResult> _results = new(results);

        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_results.TryDequeue(out var result)
                ? result
                : ToolApprovalResult.Denied("missing scripted result"));
        }
    }

    private sealed class SingleAgentToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }
}
