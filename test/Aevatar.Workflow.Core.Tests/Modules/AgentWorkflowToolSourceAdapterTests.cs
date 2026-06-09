using Aevatar.AI.Abstractions.ToolProviders;
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
                CallerCredential: new WorkflowCallerCredential { BearerToken = "token-123" }),
            CancellationToken.None);

        result.Should().Be("""{"observed":true}""");
        agentTool.ObservedArgumentsJson.Should().Be("""{"ok":true}""");
        agentTool.ObservedAccessToken.Should().Be("token-123");
        agentTool.ObservedOrgToken.Should().Be("token-123");
        agentTool.ObservedScopeId.Should().Be("scope-1");
        agentTool.ObservedCallId.Should().Be("call-1");
        agentTool.ObservedExternalMetadata.Should().NotContainKey("ExecutionId");
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

    private sealed class CapturingAgentTool : IAgentTool
    {
        public string Name => "capture_context";

        public string Description => "Capture tool context";

        public string ParametersSchema => "{}";

        public string? ObservedArgumentsJson { get; private set; }

        public string? ObservedAccessToken { get; private set; }

        public string? ObservedOrgToken { get; private set; }

        public string? ObservedScopeId { get; private set; }

        public string? ObservedCallId { get; private set; }

        public IReadOnlyDictionary<string, string> ObservedExternalMetadata { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public AgentWorkflowRuntimeContext ObservedWorkflowRuntime { get; private set; } =
            AgentWorkflowRuntimeContext.Empty;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
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
            ObservedExternalMetadata = AgentToolRequestContext.Current?.ExternalMetadata
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            ObservedWorkflowRuntime = AgentToolRequestContext.Current?.WorkflowRuntime
                ?? AgentWorkflowRuntimeContext.Empty;
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
