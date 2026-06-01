using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.AI.Core.Tests.Tools;

public sealed class AgentToolExecutionPortTests
{
    [Fact]
    public async Task AlwaysRequireDenied_ShouldRequestApprovalAndSkipTool()
    {
        var tool = new CountingAgentTool("danger", ToolApprovalMode.AlwaysRequire);
        var approval = new ScriptedApprovalHandler(ToolApprovalResult.Denied("blocked"));
        var port = new AgentToolExecutionPort(
            ToolCallMiddlewareChainFactory.ForPort([], approval, hooks: null));

        var result = await port.ExecuteAsync(Request(tool), CancellationToken.None);

        result.Status.Should().Be(AgentToolExecutionStatus.ApprovalDenied);
        result.ErrorMessage.Should().Contain("blocked");
        tool.ExecuteCalls.Should().Be(0);
        approval.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task AutoRequiresApprovalTrue_ShouldRequestApproval()
    {
        var tool = new CountingAgentTool("dynamic", ToolApprovalMode.Auto)
        {
            RuntimeDecision = true,
        };
        var approval = new ScriptedApprovalHandler(ToolApprovalResult.Approved());
        var port = new AgentToolExecutionPort(
            ToolCallMiddlewareChainFactory.ForPort([], approval, hooks: null));

        var result = await port.ExecuteAsync(Request(tool, argumentsJson: """{"method":"POST"}"""), CancellationToken.None);

        result.Status.Should().Be(AgentToolExecutionStatus.Succeeded);
        result.ResultJson.Should().Be("""{"ok":true}""");
        tool.ExecuteCalls.Should().Be(1);
        approval.Requests.Should().ContainSingle()
            .Which.ArgumentsJson.Should().Be("""{"method":"POST"}""");
    }

    [Fact]
    public async Task Approved_ShouldExecuteToolInsideAgentToolContextScope()
    {
        var tool = new ContextReadingTool("context_reader");
        var approval = new ScriptedApprovalHandler(ToolApprovalResult.Approved());
        var port = new AgentToolExecutionPort(
            ToolCallMiddlewareChainFactory.ForPort([], approval, hooks: null));
        var context = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = "token-123",
            },
        };

        var result = await port.ExecuteAsync(Request(tool, context), CancellationToken.None);

        result.Status.Should().Be(AgentToolExecutionStatus.Succeeded);
        result.ResultJson.Should().Be("token-123");
        tool.ExecuteCalls.Should().Be(1);
    }

    [Fact]
    public async Task MissingApprovalHandler_ShouldDenyApprovalRequiredTool()
    {
        var tool = new CountingAgentTool("danger", ToolApprovalMode.AlwaysRequire);
        var port = new AgentToolExecutionPort(
            ToolCallMiddlewareChainFactory.ForPort([], approvalHandler: null, hooks: null));

        var result = await port.ExecuteAsync(Request(tool), CancellationToken.None);

        result.Status.Should().Be(AgentToolExecutionStatus.ApprovalDenied);
        result.ErrorMessage.Should().Contain("approval handler is not configured");
        tool.ExecuteCalls.Should().Be(0);
    }

    [Fact]
    public async Task TimedOutApproval_ShouldReturnTimedOutAndSkipTool()
    {
        var tool = new CountingAgentTool("danger", ToolApprovalMode.AlwaysRequire);
        var approval = new ScriptedApprovalHandler(ToolApprovalResult.TimedOut("approval expired"));
        var port = new AgentToolExecutionPort(
            ToolCallMiddlewareChainFactory.ForPort([], approval, hooks: null));

        var result = await port.ExecuteAsync(Request(tool), CancellationToken.None);

        result.Status.Should().Be(AgentToolExecutionStatus.ApprovalTimedOut);
        result.ErrorMessage.Should().Be("approval expired");
        result.ResultJson.Should().Contain("approval timed out");
        tool.ExecuteCalls.Should().Be(0);
        approval.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task YieldedApproval_ShouldReturnPendingAndSkipTool()
    {
        var tool = new CountingAgentTool("danger", ToolApprovalMode.AlwaysRequire);
        var approval = new ScriptedApprovalHandler(ToolApprovalResult.Yielded("approval-request-1"));
        var port = new AgentToolExecutionPort(
            ToolCallMiddlewareChainFactory.ForPort([], approval, hooks: null));

        var result = await port.ExecuteAsync(Request(tool), CancellationToken.None);

        result.Status.Should().Be(AgentToolExecutionStatus.ApprovalPending);
        result.ErrorMessage.Should().Be("approval-request-1");
        using var payload = JsonDocument.Parse(result.ResultJson!);
        payload.RootElement.GetProperty("approval_required").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("tool_name").GetString().Should().Be("danger");
        payload.RootElement.GetProperty("tool_call_id").GetString().Should().Be("tc-1");
        payload.RootElement.GetProperty("message").GetString().Should().Contain("requires user approval");
        tool.ExecuteCalls.Should().Be(0);
        approval.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task MiddlewareTerminateWithoutApprovalKind_ShouldReturnMiddlewareTerminated()
    {
        var tool = new CountingAgentTool("blocked", ToolApprovalMode.NeverRequire);
        var port = new AgentToolExecutionPort(
            [
                new DelegateToolCallMiddleware((context, _) =>
                {
                    context.Terminate = true;
                    context.Result = """{"blocked":true}""";
                    return Task.CompletedTask;
                }),
            ]);

        var result = await port.ExecuteAsync(Request(tool), CancellationToken.None);

        result.Status.Should().Be(AgentToolExecutionStatus.MiddlewareTerminated);
        result.ResultJson.Should().Be("""{"blocked":true}""");
        tool.ExecuteCalls.Should().Be(0);
    }

    private static AgentToolExecutionRequest Request(
        IAgentTool tool,
        AgentToolExecutionContext? context = null,
        string argumentsJson = "{}") =>
        new(
            Tool: tool,
            ToolName: tool.Name,
            ToolCallId: "tc-1",
            ArgumentsJson: argumentsJson,
            ExecutionContext: context ?? AgentToolExecutionContext.Empty);

    private sealed class CountingAgentTool(string name, ToolApprovalMode approvalMode) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode { get; } = approvalMode;
        public bool? RuntimeDecision { get; init; }
        public int ExecuteCalls { get; private set; }

        public bool? RequiresApproval(string argumentsJson) => RuntimeDecision;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCalls++;
            return Task.FromResult("""{"ok":true}""");
        }
    }

    private sealed class ContextReadingTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "context reader";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;
        public int ExecuteCalls { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCalls++;
            return Task.FromResult(AgentToolRequestContext.NyxIdAccessToken ?? string.Empty);
        }
    }

    private sealed class ScriptedApprovalHandler(params ToolApprovalResult[] results) : IToolApprovalHandler
    {
        private readonly Queue<ToolApprovalResult> _results = new(results);

        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_results.TryDequeue(out var result) ? result : ToolApprovalResult.Denied());
        }
    }

    private sealed class DelegateToolCallMiddleware(
        Func<ToolCallContext, Func<Task>, Task> handler) : IToolCallMiddleware
    {
        public Task InvokeAsync(ToolCallContext context, Func<Task> next) => handler(context, next);
    }
}
