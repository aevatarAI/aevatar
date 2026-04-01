using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class ToolApprovalMiddlewareTests
{
    // ═══════════════════════════════════════════════════════════
    // ToolApprovalMiddleware — policy tests
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task NeverRequire_ShouldExecuteWithoutHandler()
    {
        var handler = new StubApprovalHandler(ToolApprovalDecision.Denied);
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("safe", ToolApprovalMode.NeverRequire);
        var context = CreateContext(tool);
        var executed = false;

        await middleware.InvokeAsync(context, () => { executed = true; return Task.CompletedTask; });

        executed.Should().BeTrue();
        handler.RequestCount.Should().Be(0, "NeverRequire should not call handler");
    }

    [Fact]
    public async Task AlwaysRequire_Approved_ShouldExecute()
    {
        var handler = new StubApprovalHandler(ToolApprovalDecision.Approved);
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("write", ToolApprovalMode.AlwaysRequire);
        var context = CreateContext(tool);
        var executed = false;

        await middleware.InvokeAsync(context, () => { executed = true; return Task.CompletedTask; });

        executed.Should().BeTrue();
        handler.RequestCount.Should().Be(1);
        context.Terminate.Should().BeFalse();
    }

    [Fact]
    public async Task AlwaysRequire_Denied_ShouldTerminate()
    {
        var handler = new StubApprovalHandler(ToolApprovalDecision.Denied, "Not allowed");
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("write", ToolApprovalMode.AlwaysRequire);
        var context = CreateContext(tool);
        var executed = false;

        await middleware.InvokeAsync(context, () => { executed = true; return Task.CompletedTask; });

        executed.Should().BeFalse();
        context.Terminate.Should().BeTrue();
        context.Result.Should().Contain("denied");
        context.Result.Should().Contain("Not allowed");
    }

    [Fact]
    public async Task AlwaysRequire_Timeout_ShouldTerminate()
    {
        var handler = new StubApprovalHandler(ToolApprovalDecision.Timeout);
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("write", ToolApprovalMode.AlwaysRequire);
        var context = CreateContext(tool);
        var executed = false;

        await middleware.InvokeAsync(context, () => { executed = true; return Task.CompletedTask; });

        executed.Should().BeFalse();
        context.Terminate.Should().BeTrue();
        context.Result.Should().Contain("timed out");
    }

    [Fact]
    public async Task Auto_ReadOnly_ShouldAutoApprove()
    {
        var handler = new StubApprovalHandler(ToolApprovalDecision.Denied);
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("search", ToolApprovalMode.Auto, isReadOnly: true);
        var context = CreateContext(tool);
        var executed = false;

        await middleware.InvokeAsync(context, () => { executed = true; return Task.CompletedTask; });

        executed.Should().BeTrue();
        handler.RequestCount.Should().Be(0, "ReadOnly + Auto should auto-approve");
    }

    [Fact]
    public async Task Auto_Destructive_ShouldRequestApproval()
    {
        var handler = new StubApprovalHandler(ToolApprovalDecision.Approved);
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("delete", ToolApprovalMode.Auto, isDestructive: true);
        var context = CreateContext(tool);
        var executed = false;

        await middleware.InvokeAsync(context, () => { executed = true; return Task.CompletedTask; });

        executed.Should().BeTrue();
        handler.RequestCount.Should().Be(1, "Destructive + Auto should request approval");
    }

    [Fact]
    public async Task Auto_Default_ShouldAutoApprove()
    {
        var handler = new StubApprovalHandler(ToolApprovalDecision.Denied);
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("misc", ToolApprovalMode.Auto); // neither ReadOnly nor Destructive
        var context = CreateContext(tool);
        var executed = false;

        await middleware.InvokeAsync(context, () => { executed = true; return Task.CompletedTask; });

        executed.Should().BeTrue();
        handler.RequestCount.Should().Be(0, "Default Auto (not destructive) should auto-approve");
    }

    [Fact]
    public async Task DenialTracking_After3Denials_ShouldAutoBlock()
    {
        var handler = new StubApprovalHandler(ToolApprovalDecision.Denied);
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("danger", ToolApprovalMode.AlwaysRequire);

        // 3 denials
        for (var i = 0; i < 3; i++)
        {
            var ctx = CreateContext(tool);
            await middleware.InvokeAsync(ctx, () => Task.CompletedTask);
            ctx.Terminate.Should().BeTrue();
        }

        // 4th call should auto-block without calling handler
        var autoBlockCtx = CreateContext(tool);
        var handlerCountBefore = handler.RequestCount;
        await middleware.InvokeAsync(autoBlockCtx, () => Task.CompletedTask);

        autoBlockCtx.Terminate.Should().BeTrue();
        autoBlockCtx.Result.Should().Contain("denied 3 times");
        handler.RequestCount.Should().Be(handlerCountBefore, "should not call handler after auto-block");
    }

    [Fact]
    public async Task DenialTracking_ShouldResetOnSuccess()
    {
        var decisions = new Queue<ToolApprovalDecision>();
        decisions.Enqueue(ToolApprovalDecision.Denied);
        decisions.Enqueue(ToolApprovalDecision.Denied);
        decisions.Enqueue(ToolApprovalDecision.Approved); // reset
        decisions.Enqueue(ToolApprovalDecision.Denied);
        decisions.Enqueue(ToolApprovalDecision.Denied);
        // Should NOT auto-block here (only 2 consecutive denials after reset)

        var handler = new QueueApprovalHandler(decisions);
        var middleware = new ToolApprovalMiddleware(handler);
        var tool = new TestTool("danger", ToolApprovalMode.AlwaysRequire);

        // 2 denials
        for (var i = 0; i < 2; i++)
            await middleware.InvokeAsync(CreateContext(tool), () => Task.CompletedTask);

        // 1 approval (resets counter)
        var approvedCtx = CreateContext(tool);
        var executed = false;
        await middleware.InvokeAsync(approvedCtx, () => { executed = true; return Task.CompletedTask; });
        executed.Should().BeTrue();

        // 2 more denials (counter restarted from 0)
        for (var i = 0; i < 2; i++)
            await middleware.InvokeAsync(CreateContext(tool), () => Task.CompletedTask);

        // This should NOT auto-block (only 2 consecutive, need 3)
        var ctx = CreateContext(tool);
        decisions.Enqueue(ToolApprovalDecision.Denied);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);
        // Now 3 consecutive → next should auto-block
        handler.RequestCount.Should().Be(6); // all 6 were sent to handler
    }

    // ═══════════════════════════════════════════════════════════
    // LocalApprovalHandler
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task LocalHandler_SubmitDecision_ShouldUnblockWaiting()
    {
        var handler = new LocalApprovalHandler();
        var published = new List<ToolApprovalRequestEvent>();
        handler.SetPublishCallback(evt => { published.Add(evt); return Task.CompletedTask; });

        var request = CreateApprovalRequest();

        // Start approval in background, then submit decision
        var approvalTask = handler.RequestApprovalAsync(request, CancellationToken.None);
        await Task.Delay(50); // let the publish callback fire

        published.Should().ContainSingle();
        published[0].RequestId.Should().Be(request.RequestId);

        handler.SubmitDecision(request.RequestId, approved: true, "Looks good");

        var result = await approvalTask;
        result.Decision.Should().Be(ToolApprovalDecision.Approved);
        result.Reason.Should().Be("Looks good");
    }

    [Fact]
    public async Task LocalHandler_Timeout_ShouldReturnTimeout()
    {
        var handler = new LocalApprovalHandler();
        handler.SetPublishCallback(_ => Task.CompletedTask);
        handler.SetTimeout(1); // 1 second timeout

        var request = CreateApprovalRequest();
        var result = await handler.RequestApprovalAsync(request, CancellationToken.None);

        result.Decision.Should().Be(ToolApprovalDecision.Timeout);
    }

    [Fact]
    public void LocalHandler_SubmitDecision_WhenNoWaiter_ShouldNotThrow()
    {
        var handler = new LocalApprovalHandler();
        var act = () => handler.SubmitDecision("nonexistent", approved: true);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task LocalHandler_WhenNoPublishCallback_ShouldDeny()
    {
        var handler = new LocalApprovalHandler();
        // No SetPublishCallback called

        var result = await handler.RequestApprovalAsync(CreateApprovalRequest(), CancellationToken.None);

        result.Decision.Should().Be(ToolApprovalDecision.Denied);
    }

    // ═══════════════════════════════════════════════════════════
    // PriorityApprovalHandler
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Priority_LocalApproved_ShouldReturnImmediately()
    {
        var local = new StubApprovalHandler(ToolApprovalDecision.Approved);
        var remote = new StubApprovalHandler(ToolApprovalDecision.Denied);
        var priority = new PriorityApprovalHandler(local, remote);

        var result = await priority.RequestApprovalAsync(CreateApprovalRequest(), CancellationToken.None);

        result.Decision.Should().Be(ToolApprovalDecision.Approved);
        local.RequestCount.Should().Be(1);
        remote.RequestCount.Should().Be(0, "remote should not be called when local approves");
    }

    [Fact]
    public async Task Priority_LocalTimeout_ShouldFallbackToRemote()
    {
        var local = new StubApprovalHandler(ToolApprovalDecision.Timeout);
        var remote = new StubApprovalHandler(ToolApprovalDecision.Approved);
        var priority = new PriorityApprovalHandler(local, remote);

        var result = await priority.RequestApprovalAsync(CreateApprovalRequest(), CancellationToken.None);

        result.Decision.Should().Be(ToolApprovalDecision.Approved);
        local.RequestCount.Should().Be(1);
        remote.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Priority_BothTimeout_ShouldReturnTimeout()
    {
        var local = new StubApprovalHandler(ToolApprovalDecision.Timeout);
        var remote = new StubApprovalHandler(ToolApprovalDecision.Timeout);
        var priority = new PriorityApprovalHandler(local, remote);

        var result = await priority.RequestApprovalAsync(CreateApprovalRequest(), CancellationToken.None);

        result.Decision.Should().Be(ToolApprovalDecision.Timeout);
    }

    [Fact]
    public async Task Priority_NoRemote_LocalTimeout_ShouldReturnTimeout()
    {
        var local = new StubApprovalHandler(ToolApprovalDecision.Timeout);
        var priority = new PriorityApprovalHandler(local, remoteHandler: null);

        var result = await priority.RequestApprovalAsync(CreateApprovalRequest(), CancellationToken.None);

        result.Decision.Should().Be(ToolApprovalDecision.Timeout);
    }

    // ═══════════════════════════════════════════════════════════
    // IAgentTool metadata defaults
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DefaultTool_ShouldHaveCorrectDefaults()
    {
        IAgentTool tool = new TestTool("test", ToolApprovalMode.NeverRequire);
        tool.IsReadOnly.Should().BeFalse();
        tool.IsDestructive.Should().BeFalse();
        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
    }

    // ═══════════════════════════════════════════════════════════
    // Integration: ToolCallLoop with approval middleware
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ToolCallLoop_WithApproval_ShouldBlockDeniedTool()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall { Id = "tc-1", Name = "danger", ArgumentsJson = "{}" },
                ],
            },
            new LLMResponse { Content = "ok" },
        ]);

        var tools = new ToolManager();
        tools.Register(new TestTool("danger", ToolApprovalMode.AlwaysRequire));

        var handler = new StubApprovalHandler(ToolApprovalDecision.Denied, "Blocked by policy");
        var approvalMiddleware = new ToolApprovalMiddleware(handler);

        var loop = new ToolCallLoop(tools, toolMiddlewares: [approvalMiddleware]);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        result.Should().Be("ok");
        // The tool result should contain denial message
        messages.Should().Contain(m => m.Role == "tool" && m.Content != null && m.Content.Contains("denied"));
    }

    [Fact]
    public async Task ApprovalHooks_ShouldFire()
    {
        var hook = new ApprovalRecordingHook();
        var pipeline = new AgentHookPipeline([hook]);
        var handler = new StubApprovalHandler(ToolApprovalDecision.Approved);
        var middleware = new ToolApprovalMiddleware(handler, pipeline);
        var tool = new TestTool("test", ToolApprovalMode.AlwaysRequire);
        var context = CreateContext(tool);

        await middleware.InvokeAsync(context, () => Task.CompletedTask);

        hook.ApprovalRequestedCount.Should().Be(1);
        hook.ApprovalCompletedCount.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════

    private static ToolCallContext CreateContext(IAgentTool tool) => new()
    {
        Tool = tool,
        ToolName = tool.Name,
        ToolCallId = $"tc-{Guid.NewGuid():N}",
        ArgumentsJson = "{}",
    };

    private static ToolApprovalRequest CreateApprovalRequest() => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        ToolName = "test_tool",
        ToolCallId = "tc-1",
        ArgumentsJson = "{}",
        ApprovalMode = ToolApprovalMode.AlwaysRequire,
    };

    // ═══════════════════════════════════════════════════════════
    // Test Doubles
    // ═══════════════════════════════════════════════════════════

    private sealed class StubApprovalHandler(
        ToolApprovalDecision decision,
        string? reason = null) : IToolApprovalHandler
    {
        public int RequestCount { get; private set; }

        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            RequestCount++;
            return Task.FromResult(new ToolApprovalResult { Decision = decision, Reason = reason });
        }
    }

    private sealed class QueueApprovalHandler(Queue<ToolApprovalDecision> decisions) : IToolApprovalHandler
    {
        public int RequestCount { get; private set; }

        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            RequestCount++;
            var decision = decisions.Count > 0 ? decisions.Dequeue() : ToolApprovalDecision.Denied;
            return Task.FromResult(new ToolApprovalResult { Decision = decision });
        }
    }

    private sealed class TestTool(
        string name,
        ToolApprovalMode approvalMode,
        bool isReadOnly = false,
        bool isDestructive = false) : IAgentTool
    {
        public string Name => name;
        public string Description => "test";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => approvalMode;
        public bool IsReadOnly => isReadOnly;
        public bool IsDestructive => isDestructive;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult($"executed:{argumentsJson}");
    }

    private sealed class QueueLLMProvider(IEnumerable<LLMResponse> responses) : ILLMProvider
    {
        private readonly Queue<LLMResponse> _responses = new(responses);
        public string Name => "queue";

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default) =>
            Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LLMResponse());

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ApprovalRecordingHook : IAIGAgentExecutionHook
    {
        public string Name => "approval_recorder";
        public int Priority => 0;

        public int ApprovalRequestedCount { get; private set; }
        public int ApprovalCompletedCount { get; private set; }

        public Task OnToolApprovalRequestedAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ApprovalRequestedCount++;
            return Task.CompletedTask;
        }

        public Task OnToolApprovalCompletedAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ApprovalCompletedCount++;
            return Task.CompletedTask;
        }
    }
}
