using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Core.Middleware;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Middleware;

public class MiddlewarePipelineTests
{
    // ─── Agent Run Middleware ───

    [Fact]
    public async Task RunAgentAsync_NoMiddleware_ExecutesCoreHandler()
    {
        var executed = false;
        var ctx = new AgentRunContext { UserMessage = "hello" };

        await MiddlewarePipeline.RunAgentAsync([], ctx, () =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        executed.Should().BeTrue();
    }

    [Fact]
    public async Task RunAgentAsync_MiddlewareChain_ExecutesInOrder()
    {
        var order = new List<string>();

        var mw1 = new DelegateAgentRunMiddleware(async (ctx, next) =>
        {
            order.Add("mw1-before");
            await next();
            order.Add("mw1-after");
        });
        var mw2 = new DelegateAgentRunMiddleware(async (ctx, next) =>
        {
            order.Add("mw2-before");
            await next();
            order.Add("mw2-after");
        });

        var ctx = new AgentRunContext { UserMessage = "test" };
        await MiddlewarePipeline.RunAgentAsync([mw1, mw2], ctx, () =>
        {
            order.Add("core");
            return Task.CompletedTask;
        });

        order.Should().Equal("mw1-before", "mw2-before", "core", "mw2-after", "mw1-after");
    }

    [Fact]
    public async Task RunAgentAsync_MiddlewareCanShortCircuit()
    {
        var coreExecuted = false;
        var mw = new DelegateAgentRunMiddleware((ctx, next) =>
        {
            ctx.Terminate = true;
            ctx.Result = "blocked";
            return Task.CompletedTask;
        });

        var ctx = new AgentRunContext { UserMessage = "test" };
        await MiddlewarePipeline.RunAgentAsync([mw], ctx, () =>
        {
            coreExecuted = true;
            return Task.CompletedTask;
        });

        coreExecuted.Should().BeFalse();
        ctx.Terminate.Should().BeTrue();
        ctx.Result.Should().Be("blocked");
    }

    [Fact]
    public async Task RunAgentAsync_MiddlewareCanModifyInput()
    {
        string? capturedMessage = null;
        var mw = new DelegateAgentRunMiddleware(async (ctx, next) =>
        {
            ctx.UserMessage = ctx.UserMessage.ToUpperInvariant();
            await next();
        });

        var ctx = new AgentRunContext { UserMessage = "hello" };
        await MiddlewarePipeline.RunAgentAsync([mw], ctx, () =>
        {
            capturedMessage = ctx.UserMessage;
            return Task.CompletedTask;
        });

        capturedMessage.Should().Be("HELLO");
    }

    // ─── Tool Call Middleware ───

    [Fact]
    public async Task RunToolCallAsync_Chain_ExecutesInOrder()
    {
        var order = new List<string>();
        var mw = new DelegateToolCallMiddleware(async (ctx, next) =>
        {
            order.Add("before");
            await next();
            order.Add("after");
        });

        var tool = new FakeTool("test");
        var ctx = new ToolCallContext
        {
            Tool = tool, ToolName = "test", ToolCallId = "id1", ArgumentsJson = "{}",
        };

        await MiddlewarePipeline.RunToolCallAsync([mw], ctx, () =>
        {
            order.Add("core");
            ctx.Result = "result";
            return Task.CompletedTask;
        });

        order.Should().Equal("before", "core", "after");
        ctx.Result.Should().Be("result");
    }

    [Fact]
    public async Task RunToolCallAsync_MiddlewareCanOverrideResult()
    {
        var mw = new DelegateToolCallMiddleware(async (ctx, next) =>
        {
            await next();
            ctx.Result = "overridden";
        });

        var tool = new FakeTool("test");
        var ctx = new ToolCallContext
        {
            Tool = tool, ToolName = "test", ToolCallId = "id1", ArgumentsJson = "{}",
        };

        await MiddlewarePipeline.RunToolCallAsync([mw], ctx, () =>
        {
            ctx.Result = "original";
            return Task.CompletedTask;
        });

        ctx.Result.Should().Be("overridden");
    }

    // ─── LLM Call Middleware ───

    [Fact]
    public async Task RunLLMCallAsync_Chain_ExecutesInOrder()
    {
        var order = new List<string>();
        var mw = new DelegateLLMCallMiddleware(async (ctx, next) =>
        {
            order.Add("before");
            await next();
            order.Add("after");
        });

        var ctx = new LLMCallContext
        {
            Request = new Aevatar.AI.Abstractions.LLMProviders.LLMRequest { Messages = [] },
            Provider = new FakeLLMProvider(),
        };

        await MiddlewarePipeline.RunLLMCallAsync([mw], ctx, () =>
        {
            order.Add("core");
            return Task.CompletedTask;
        });

        order.Should().Equal("before", "core", "after");
    }

    [Fact]
    public async Task RunLLMCallAsync_MiddlewareCanShortCircuit()
    {
        var coreExecuted = false;
        var mw = new DelegateLLMCallMiddleware((ctx, _) =>
        {
            ctx.Terminate = true;
            ctx.Response = new Aevatar.AI.Abstractions.LLMProviders.LLMResponse { Content = "cached" };
            return Task.CompletedTask;
        });

        var ctx = new LLMCallContext
        {
            Request = new Aevatar.AI.Abstractions.LLMProviders.LLMRequest { Messages = [] },
            Provider = new FakeLLMProvider(),
        };

        await MiddlewarePipeline.RunLLMCallAsync([mw], ctx, () =>
        {
            coreExecuted = true;
            return Task.CompletedTask;
        });

        coreExecuted.Should().BeFalse();
        ctx.Response!.Content.Should().Be("cached");
    }

    // ─── Item Sharing ───

    [Fact]
    public async Task AgentRunContext_ItemsSharedAcrossMiddleware()
    {
        var mw1 = new DelegateAgentRunMiddleware(async (ctx, next) =>
        {
            ctx.Items["key"] = "value";
            await next();
        });
        var mw2 = new DelegateAgentRunMiddleware(async (ctx, next) =>
        {
            ctx.Items["key"].Should().Be("value");
            ctx.Items["key2"] = "value2";
            await next();
        });

        var ctx = new AgentRunContext { UserMessage = "test" };
        object? capturedKey2 = null;

        await MiddlewarePipeline.RunAgentAsync([mw1, mw2], ctx, () =>
        {
            capturedKey2 = ctx.Items["key2"];
            return Task.CompletedTask;
        });

        capturedKey2.Should().Be("value2");
    }

    // ─── Test doubles ───

    private sealed class DelegateAgentRunMiddleware(
        Func<AgentRunContext, Func<Task>, Task> handler) : IAgentRunMiddleware
    {
        public Task InvokeAsync(AgentRunContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class DelegateToolCallMiddleware(
        Func<ToolCallContext, Func<Task>, Task> handler) : IToolCallMiddleware
    {
        public Task InvokeAsync(ToolCallContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class DelegateLLMCallMiddleware(
        Func<LLMCallContext, Func<Task>, Task> handler) : ILLMCallMiddleware
    {
        public Task InvokeAsync(LLMCallContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class FakeTool(string name) : Aevatar.AI.Abstractions.ToolProviders.IAgentTool
    {
        public string Name => name;
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct) =>
            Task.FromResult("fake-result");
    }

    private sealed class FakeLLMProvider : Aevatar.AI.Abstractions.LLMProviders.ILLMProvider
    {
        public string Name => "fake";

        public Task<Aevatar.AI.Abstractions.LLMProviders.LLMResponse> ChatAsync(
            Aevatar.AI.Abstractions.LLMProviders.LLMRequest request, CancellationToken ct) =>
            Task.FromResult(new Aevatar.AI.Abstractions.LLMProviders.LLMResponse { Content = "response" });

        public IAsyncEnumerable<Aevatar.AI.Abstractions.LLMProviders.LLMStreamChunk> ChatStreamAsync(
            Aevatar.AI.Abstractions.LLMProviders.LLMRequest request, CancellationToken ct) =>
            AsyncEnumerable.Empty<Aevatar.AI.Abstractions.LLMProviders.LLMStreamChunk>();
    }
}
