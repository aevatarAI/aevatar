using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class ToolCallLoopTests
{
    [Fact]
    public async Task ExecuteAsync_WhenNoToolCalls_ShouldReturnAssistantContent()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "final-answer" },
        ]);
        var loop = new ToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("final-answer");
        messages.Should().ContainSingle(m => m.Role == "assistant" && m.Content == "final-answer");
        provider.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolCallThenFollowUp_ShouldExecuteToolAndReturnFinalContent()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-1",
                        Name = "echo",
                        ArgumentsJson = """{"q":"abc"}""",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", args => $"RESULT:{args}"));
        var loop = new ToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        result.Should().Be("done");
        messages.Any(m => m.Role == "assistant" && m.ToolCalls?.Count == 1).Should().BeTrue();
        messages.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "tc-1" && m.Content == """RESULT:{"q":"abc"}""");
        messages.Should().Contain(m => m.Role == "assistant" && m.Content == "done");
    }

    [Fact]
    public async Task ExecuteAsync_WhenHookMutatesToolCall_ShouldUseMutatedNameAndArguments()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-2",
                        Name = "original",
                        ArgumentsJson = """{"x":1}""",
                    },
                ],
            },
            new LLMResponse { Content = "ok" },
        ]);

        var capturedArguments = string.Empty;
        var tools = new ToolManager();
        tools.Register(new DelegateTool("mutated", args =>
        {
            capturedArguments = args;
            return "mutated-result";
        }));

        var hook = new RecordingHook
        {
            OnToolStart = ctx =>
            {
                ctx.ToolName = "mutated";
                ctx.ToolArguments = """{"x":999}""";
            },
        };
        var hooks = new AgentHookPipeline([hook]);
        var loop = new ToolCallLoop(tools, hooks);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("ok");
        capturedArguments.Should().Be("""{"x":999}""");
        hook.ToolStartCount.Should().Be(1);
        hook.ToolEndCount.Should().Be(1);
        hook.ToolResultAtEnd.Should().Be("mutated-result");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxRoundsReachedWithoutTerminalContent_ShouldReturnNull()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-3",
                        Name = "echo",
                        ArgumentsJson = "{}",
                    },
                ],
            },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "{}"));
        var loop = new ToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        result.Should().BeNull();
        messages.Count(m => m.Role == "assistant" && m.ToolCalls?.Count == 1).Should().Be(1);
        messages.Should().ContainSingle(m => m.Role == "tool");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldInvokeLLMHookLifecycle()
    {
        var provider = new QueueLLMProvider([new LLMResponse { Content = "ok" }]);
        var hook = new RecordingHook();
        var loop = new ToolCallLoop(new ToolManager(), new AgentHookPipeline([hook]));
        var messages = new List<ChatMessage> { ChatMessage.User("u") };
        var request = new LLMRequest { Messages = [], Tools = null };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        hook.LlmStartCount.Should().Be(1);
        hook.LlmEndCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolMiddlewareRewritesArguments_ShouldUseRewrittenArguments()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-arg-rewrite",
                        Name = "echo",
                        ArgumentsJson = """{"v":"original"}""",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);

        var capturedArguments = string.Empty;
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", args =>
        {
            capturedArguments = args;
            return args;
        }));

        var rewriteMiddleware = new DelegateToolCallMiddleware(async (ctx, next) =>
        {
            ctx.ArgumentsJson = """{"v":"rewritten"}""";
            await next();
        });

        var loop = new ToolCallLoop(
            tools,
            hooks: null,
            toolMiddlewares: [rewriteMiddleware],
            llmMiddlewares: []);

        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("done");
        capturedArguments.Should().Be("""{"v":"rewritten"}""");
        messages.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "tc-arg-rewrite" && m.Content == """{"v":"rewritten"}""");
    }

    private sealed class QueueLLMProvider : ILLMProvider
    {
        private readonly Queue<LLMResponse> _responses;

        public QueueLLMProvider(IEnumerable<LLMResponse> responses)
        {
            _responses = new Queue<LLMResponse>(responses);
        }

        public string Name => "queue";
        public List<LLMRequest> Requests { get; } = [];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LLMResponse());
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class DelegateTool : IAgentTool
    {
        private readonly Func<string, string> _execute;

        public DelegateTool(string name, Func<string, string> execute)
        {
            Name = name;
            _execute = execute;
        }

        public string Name { get; }
        public string Description => "delegate";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_execute(argumentsJson));
        }
    }

    private sealed class DelegateToolCallMiddleware(
        Func<ToolCallContext, Func<Task>, Task> handler) : IToolCallMiddleware
    {
        public Task InvokeAsync(ToolCallContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class RecordingHook : IAIGAgentExecutionHook
    {
        public string Name => "rec";
        public int Priority => 0;

        public int LlmStartCount { get; private set; }
        public int LlmEndCount { get; private set; }
        public int ToolStartCount { get; private set; }
        public int ToolEndCount { get; private set; }
        public string? ToolResultAtEnd { get; private set; }
        public Action<AIGAgentExecutionHookContext>? OnToolStart { get; init; }

        public Task OnLLMRequestStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = ctx;
            LlmStartCount++;
            return Task.CompletedTask;
        }

        public Task OnLLMRequestEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = ctx;
            LlmEndCount++;
            return Task.CompletedTask;
        }

        public Task OnToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ToolStartCount++;
            OnToolStart?.Invoke(ctx);
            return Task.CompletedTask;
        }

        public Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ToolEndCount++;
            ToolResultAtEnd = ctx.ToolResult;
            return Task.CompletedTask;
        }
    }
}
