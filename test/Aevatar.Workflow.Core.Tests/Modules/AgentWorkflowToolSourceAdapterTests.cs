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
            ObservedAccessToken = AgentToolRequestContext.AccessToken;
            ObservedOrgToken = AgentToolRequestContext.OrganizationToken;
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
