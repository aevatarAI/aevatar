using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Middleware;

public class ToolApprovalMiddlewareTests
{
    [Fact]
    public async Task NeverRequireMode_BypassesApprovalAndExecutesNext()
    {
        var handler = new ScriptedApprovalHandler();
        var middleware = new ToolApprovalMiddleware(handler);
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("search", ToolApprovalMode.NeverRequire),
            ToolName = "search",
            ToolCallId = "tc-1",
            ArgumentsJson = "{}",
        };

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
        ctx.Terminate.Should().BeFalse();
    }

    [Fact]
    public async Task RuntimeRequiresApproval_False_BypassesApproval()
    {
        var handler = new ScriptedApprovalHandler(ToolApprovalResult.Denied());
        var middleware = new ToolApprovalMiddleware(handler);
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("read-only", ToolApprovalMode.AlwaysRequire)
            {
                RuntimeDecision = false,
            },
            ToolName = "read-only",
            ToolCallId = "tc-2",
            ArgumentsJson = "{}",
        };

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AutoMode_ReadOnlyTool_BypassesApproval()
    {
        var handler = new ScriptedApprovalHandler(ToolApprovalResult.Denied());
        var middleware = new ToolApprovalMiddleware(handler);
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("ro", ToolApprovalMode.Auto) { IsReadOnly = true },
            ToolName = "ro",
            ToolCallId = "tc-3",
            ArgumentsJson = "{}",
        };

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AutoMode_NonReadOnlyNonDestructive_BypassesApproval()
    {
        var handler = new ScriptedApprovalHandler(ToolApprovalResult.Denied());
        var middleware = new ToolApprovalMiddleware(handler);
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("safe", ToolApprovalMode.Auto)
            {
                IsReadOnly = false,
                IsDestructive = false,
            },
            ToolName = "safe",
            ToolCallId = "tc-4",
            ArgumentsJson = "{}",
        };

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HooksRunOnRequestAndCompleted_AndApprovedExecutesNext()
    {
        var hook = new CapturingHook();
        var pipeline = new AgentHookPipeline([hook]);
        var handler = new ScriptedApprovalHandler(ToolApprovalResult.Approved("go"));
        var middleware = new ToolApprovalMiddleware(handler, pipeline);

        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("danger", ToolApprovalMode.AlwaysRequire) { IsDestructive = true },
            ToolName = "danger",
            ToolCallId = "tc-5",
            ArgumentsJson = "{\"x\":1}",
        };

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeTrue();
        ctx.Terminate.Should().BeFalse();
        hook.RequestedCalls.Should().Be(1);
        hook.CompletedCalls.Should().Be(1);
        hook.LastRequested?.Items["approval_request_id"].Should().NotBeNull();
        hook.LastRequested?.Items["approval_mode"].Should().Be(ToolApprovalMode.AlwaysRequire.ToString());
        hook.LastCompleted?.Items["approval_decision"].Should().Be(ToolApprovalDecision.Approved.ToString());
    }

    [Fact]
    public async Task DeniedDecision_StopsExecutionAndRecordsResult()
    {
        var middleware = new ToolApprovalMiddleware(new ScriptedApprovalHandler(ToolApprovalResult.Denied("blocked")));
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("danger", ToolApprovalMode.AlwaysRequire) { IsDestructive = true },
            ToolName = "danger",
            ToolCallId = "tc-6",
            ArgumentsJson = "{\"confirm\":true}",
        };

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeFalse();
        ctx.Terminate.Should().BeTrue();
        ctx.Result.Should().Contain("Tool 'danger' execution denied: blocked");
    }

    [Fact]
    public async Task TimeoutDecision_ReturnsTimeoutResultAndStopsExecution()
    {
        var middleware = new ToolApprovalMiddleware(new ScriptedApprovalHandler(ToolApprovalResult.TimedOut()));
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("danger", ToolApprovalMode.AlwaysRequire) { IsDestructive = true },
            ToolName = "danger",
            ToolCallId = "tc-7",
            ArgumentsJson = "{}",
        };

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Terminate.Should().BeTrue();
        ctx.Result.Should().Contain("approval timed out");
    }

    [Fact]
    public async Task YieldDecision_ReturnsPendingPayloadAndStopsExecution()
    {
        var middleware = new ToolApprovalMiddleware(new ScriptedApprovalHandler(ToolApprovalResult.Yielded("req-1")));
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("danger", ToolApprovalMode.AlwaysRequire) { IsDestructive = true },
            ToolName = "danger",
            ToolCallId = "tc-8",
            ArgumentsJson = "{}",
        };

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Terminate.Should().BeTrue();
        ctx.Result.Should().Contain("\"approval_required\":true");
        ctx.Result.Should().Contain("\"request_id\":\"");
    }

    [Fact]
    public async Task ConsecutiveDenialsEventuallyBlockExecution()
    {
        var handler = new ScriptedApprovalHandler(
            ToolApprovalResult.Denied("first"),
            ToolApprovalResult.Denied("second"),
            ToolApprovalResult.Denied("third"));
        var middleware = new ToolApprovalMiddleware(handler);

        var ctx1 = NewContext("danger", "tc-9");
        await middleware.InvokeAsync(ctx1, () => Task.CompletedTask);
        var ctx2 = NewContext("danger", "tc-10");
        await middleware.InvokeAsync(ctx2, () => Task.CompletedTask);
        var ctx3 = NewContext("danger", "tc-11");
        await middleware.InvokeAsync(ctx3, () => Task.CompletedTask);
        var ctx4 = NewContext("danger", "tc-12");
        await middleware.InvokeAsync(ctx4, () => Task.CompletedTask);

        ctx3.Result.Should().Contain("execution denied");
        ctx3.Terminate.Should().BeTrue();
        ctx4.Terminate.Should().BeTrue();
        ctx4.Result.Should().Contain("has been denied 3 times");
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ApprovalResetAfterApprovedAllowsNextAttempt()
    {
        var handler = new ScriptedApprovalHandler(
            ToolApprovalResult.Denied(),
            ToolApprovalResult.Denied(),
            ToolApprovalResult.Approved(),
            ToolApprovalResult.Denied("after"));

        var middleware = new ToolApprovalMiddleware(handler);

        await middleware.InvokeAsync(NewContext("danger", "tc-13"), () => Task.CompletedTask);
        await middleware.InvokeAsync(NewContext("danger", "tc-14"), () => Task.CompletedTask);

        var nextExecuted = false;
        await middleware.InvokeAsync(NewContext("danger", "tc-15"), () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        var final = NewContext("danger", "tc-16");
        await middleware.InvokeAsync(final, () => Task.CompletedTask);

        nextExecuted.Should().BeTrue();
        final.Terminate.Should().BeTrue();
        final.Result.Should().Contain("execution denied");
        final.Result.Should().Contain("after");
        final.Result.Should().NotContain("Automatic block");
        handler.Requests.Should().HaveCount(4);
    }

    private static ToolCallContext NewContext(string toolName, string callId) => new()
    {
        Tool = new FakeAgentTool(toolName, ToolApprovalMode.AlwaysRequire) { IsDestructive = true },
        ToolName = toolName,
        ToolCallId = callId,
        ArgumentsJson = "{}",
    };

    private sealed class ScriptedApprovalHandler(params ToolApprovalResult[] results) : IToolApprovalHandler
    {
        private readonly Queue<ToolApprovalResult> _results = new(results);

        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_results.TryDequeue(out var r) ? r : ToolApprovalResult.Denied("no-script"));
        }
    }

    private sealed class FakeAgentTool(string name, ToolApprovalMode approvalMode) : Aevatar.AI.Abstractions.ToolProviders.IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode { get; } = approvalMode;
        public bool IsReadOnly { get; init; }
        public bool IsDestructive { get; init; }
        public bool? RuntimeDecision { get; init; }
        public bool? RequiresApproval(string argumentsJson) => RuntimeDecision;
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) => Task.FromResult("{}");
    }

    private sealed class CapturingHook : Aevatar.AI.Core.Hooks.IAIGAgentExecutionHook
    {
        public string Name => "capturing";
        public int Priority => 0;

        public int RequestedCalls { get; private set; }
        public int CompletedCalls { get; private set; }
        public AIGAgentExecutionHookContext? LastRequested { get; private set; }
        public AIGAgentExecutionHookContext? LastCompleted { get; private set; }

        public Task OnToolApprovalRequestedAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            RequestedCalls++;
            LastRequested = ctx;
            return Task.CompletedTask;
        }

        public Task OnToolApprovalCompletedAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            CompletedCalls++;
            LastCompleted = ctx;
            return Task.CompletedTask;
        }
    }
}
