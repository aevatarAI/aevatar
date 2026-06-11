using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Middleware;

public class ToolApprovalMiddlewareTests
{
    [Fact]
    public async Task ForAgentRuntime_NullApprovalHandler_DeniesAlwaysRequireTool()
    {
        var middlewares = ToolCallMiddlewareChainFactory.ForAgentRuntime([], null, null);
        var ctx = NewContext("danger", "tc-factory-1");

        var nextExecuted = false;
        await InvokeChainAsync(middlewares, ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeFalse();
        ctx.Terminate.Should().BeTrue();
        ctx.TerminationKind.Should().Be(ToolCallTerminationKind.ApprovalDenied);
        ctx.TerminationKind.Should().NotBe(ToolCallTerminationKind.ApprovalPending);
        ctx.PendingApproval.Should().BeNull();
        ctx.Result.Should().Contain("approval-gated tools cannot run here");
    }

    [Fact]
    public async Task ForAgentRuntime_NullApprovalHandler_AllowsNeverRequireTool()
    {
        var duplicateHandler = new ScriptedApprovalHandler(ToolApprovalResult.Denied("duplicate"));
        var middlewares = ToolCallMiddlewareChainFactory.ForAgentRuntime(
            [new ToolApprovalMiddleware(duplicateHandler)],
            null,
            null);
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("search", ToolApprovalMode.NeverRequire),
            ToolName = "search",
            ToolCallId = "tc-factory-2",
            ArgumentsJson = "{}",
        };

        var nextExecuted = false;
        await InvokeChainAsync(middlewares, ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeTrue();
        ctx.Terminate.Should().BeFalse();
        duplicateHandler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ForAgentRuntime_NullApprovalHandler_DeniesAutoToolWhenRuntimeRequiresApprovalOrDestructive(
        bool requiresApproval,
        bool isDestructive)
    {
        var middlewares = ToolCallMiddlewareChainFactory.ForAgentRuntime([], null, null);
        var ctx = new ToolCallContext
        {
            Tool = new FakeAgentTool("auto-danger", ToolApprovalMode.Auto)
            {
                RuntimeDecision = requiresApproval ? true : null,
                IsDestructive = isDestructive,
            },
            ToolName = "auto-danger",
            ToolCallId = "tc-factory-3",
            ArgumentsJson = "{}",
        };

        var nextExecuted = false;
        await InvokeChainAsync(middlewares, ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeFalse();
        ctx.Terminate.Should().BeTrue();
        ctx.TerminationKind.Should().Be(ToolCallTerminationKind.ApprovalDenied);
        ctx.TerminationKind.Should().NotBe(ToolCallTerminationKind.ApprovalPending);
        ctx.PendingApproval.Should().BeNull();
        ctx.Result.Should().Contain("approval-gated tools cannot run here");
    }

    [Fact]
    public void Factory_DoesNotExposeForPort()
    {
        typeof(ToolCallMiddlewareChainFactory)
            .GetMethods()
            .Where(method => method.Name == "ForPort")
            .Should()
            .BeEmpty();
    }

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
        ctx.TerminationKind.Should().Be(ToolCallTerminationKind.ApprovalDenied);
        ctx.TerminationReason.Should().Be("blocked");
        ctx.Result.Should().Contain("Tool 'danger' execution denied: blocked");
        ctx.Receipt.Should().NotBeNull();
        ctx.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Denied);
        ctx.Receipt.ToolName.Should().Be("danger");
        ctx.Receipt.CallId.Should().Be("tc-6");
        ctx.Receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.AlwaysRequire);
        ctx.Receipt.ApprovalRequestId.Should().NotBeNullOrWhiteSpace();
        ctx.Receipt.ErrorCode.Should().Be("approval_denied");
        ctx.Receipt.ErrorMessage.Should().Be("blocked");
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
        ctx.TerminationKind.Should().Be(ToolCallTerminationKind.ApprovalTimedOut);
        ctx.Result.Should().Contain("approval timed out");
        ctx.Receipt.Should().NotBeNull();
        ctx.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        ctx.Receipt.ErrorCode.Should().Be("approval_timeout");
        ctx.Receipt.ErrorMessage.Should().Be("Tool approval timed out.");
        ctx.Receipt.ApprovalRequestId.Should().NotBeNullOrWhiteSpace();
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

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeFalse();
        ctx.Terminate.Should().BeTrue();
        ctx.TerminationKind.Should().Be(ToolCallTerminationKind.ApprovalPending);
        ctx.TerminationReason.Should().Be("req-1");
        ctx.Result.Should().Contain("\"approval_required\":true");
        ctx.Result.Should().Contain("\"request_id\":\"");
        ctx.Receipt.Should().NotBeNull();
        ctx.Receipt!.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        ctx.Receipt.ToolName.Should().Be("danger");
        ctx.Receipt.CallId.Should().Be("tc-8");
        ctx.Receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.AlwaysRequire);
        ctx.Receipt.IsDestructive.Should().BeTrue();
        ctx.Receipt.ApprovalRequestId.Should().NotBeNullOrWhiteSpace();
        ctx.Receipt.ResultJson.Should().Be(ctx.Result);
        ctx.PendingApproval.Should().NotBeNull();
        ctx.PendingApproval!.ApprovalRequestId.Should().NotBeNullOrWhiteSpace();
        ctx.Receipt.ApprovalRequestId.Should().Be(ctx.PendingApproval.ApprovalRequestId);
        ctx.PendingApproval.ToolCallId.Should().Be("tc-8");
        ctx.PendingApproval.ToolName.Should().Be("danger");
        ctx.PendingApproval.ArgumentsJson.Should().Be("{}");
        ctx.PendingApproval.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        ctx.PendingApproval.IsReadOnly.Should().BeFalse();
        ctx.PendingApproval.IsDestructive.Should().BeTrue();
    }

    [Fact]
    public async Task ApprovedGrant_ExecutesNextWithoutRequestingApprovalAgain()
    {
        var handler = new ScriptedApprovalHandler(ToolApprovalResult.Denied("would-loop"));
        var middleware = new ToolApprovalMiddleware(handler);
        var ctx = NewContext("danger", "tc-grant-1", new ToolApprovalGrantContext("approval-1", true));

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeTrue();
        ctx.Terminate.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RejectedGrant_FailsClosedWithoutRequestingApprovalAgain()
    {
        var handler = new ScriptedApprovalHandler(ToolApprovalResult.Approved());
        var middleware = new ToolApprovalMiddleware(handler);
        var ctx = NewContext("danger", "tc-grant-2", new ToolApprovalGrantContext("approval-2", false));

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeFalse();
        ctx.Terminate.Should().BeTrue();
        ctx.TerminationKind.Should().Be(ToolCallTerminationKind.ApprovalDenied);
        ctx.Receipt.Should().NotBeNull();
        ctx.Receipt!.ApprovalRequestId.Should().Be("approval-2");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestScopedDenialCountBlocksExecutionWithoutCallingHandler()
    {
        var handler = new ScriptedApprovalHandler(ToolApprovalResult.Approved());
        var middleware = new ToolApprovalMiddleware(handler);

        var ctx = NewContext("danger", "tc-9");
        ctx.Items[ToolApprovalMiddleware.DenialCountItemKey] = 3;

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Terminate.Should().BeTrue();
        ctx.TerminationKind.Should().Be(ToolCallTerminationKind.ApprovalDenied);
        ctx.TerminationReason.Should().Contain("denied 3 times");
        ctx.Result.Should().Contain("has been denied 3 times");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DenialCount_IsReturnedOnCurrentRequestContextOnly()
    {
        var handler = new ScriptedApprovalHandler(
            ToolApprovalResult.Denied("first"),
            ToolApprovalResult.Approved(),
            ToolApprovalResult.Denied("fresh"));

        var middleware = new ToolApprovalMiddleware(handler);

        var denied = NewContext("danger", "tc-13");
        await middleware.InvokeAsync(denied, () => Task.CompletedTask);
        denied.Items[ToolApprovalMiddleware.DenialCountItemKey].Should().Be(1);

        var nextExecuted = false;
        await middleware.InvokeAsync(NewContext("danger", "tc-15"), () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        var final = NewContext("danger", "tc-16");
        final.Items[ToolApprovalMiddleware.DenialCountItemKey] = 0;
        await middleware.InvokeAsync(final, () => Task.CompletedTask);

        nextExecuted.Should().BeTrue();
        final.Items[ToolApprovalMiddleware.DenialCountItemKey].Should().Be(1);
        final.Terminate.Should().BeTrue();
        final.Result.Should().Contain("execution denied");
        final.Result.Should().Contain("fresh");
        final.Result.Should().NotContain("Automatic block");
        handler.Requests.Should().HaveCount(3);
    }

    private static ToolCallContext NewContext(
        string toolName,
        string callId,
        ToolApprovalGrantContext? approvalGrant = null) => new()
    {
        Tool = new FakeAgentTool(toolName, ToolApprovalMode.AlwaysRequire) { IsDestructive = true },
        ToolName = toolName,
        ToolCallId = callId,
        ArgumentsJson = "{}",
        ApprovalGrant = approvalGrant,
    };

    private static Task InvokeChainAsync(
        IReadOnlyList<IToolCallMiddleware> middlewares,
        ToolCallContext context,
        Func<Task> terminal)
    {
        var next = terminal;

        for (var i = middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = middlewares[i];
            var currentNext = next;
            next = () => middleware.InvokeAsync(context, currentNext);
        }

        return next();
    }

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
