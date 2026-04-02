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
    public async Task ExecuteAsync_WhenBaseRequestIdPresent_ShouldKeepStableRequestIdAndEmitPerCallMetadata()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-identity",
                        Name = "echo",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "{}"));
        var loop = new ToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest
        {
            Messages = [],
            RequestId = "session-99",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow.run_id"] = "run-99",
            },
        };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].RequestId.Should().Be("session-99");
        provider.Requests[1].RequestId.Should().Be("session-99");
        provider.Requests.Should().OnlyContain(x => x.Metadata != null && x.Metadata["workflow.run_id"] == "run-99");
        provider.Requests[0].Metadata![LLMRequestMetadataKeys.CallId].Should().Be("session-99");
        provider.Requests[1].Metadata![LLMRequestMetadataKeys.CallId].Should().Be("session-99:tool-round:2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenBaseRequestIdPresent_ShouldExposeStableRequestIdAndPerCallIdToLlmMiddlewareMetadata()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-identity",
                        Name = "echo",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "{}"));
        var requestIdMiddleware = new CaptureLlmRequestIdentityMiddleware();
        var loop = new ToolCallLoop(
            tools,
            hooks: null,
            toolMiddlewares: [],
            llmMiddlewares: [requestIdMiddleware]);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest
        {
            Messages = [],
            RequestId = "session-105",
        };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        requestIdMiddleware.RequestIds.Should().Equal("session-105", "session-105");
        requestIdMiddleware.CallIds.Should().Equal("session-105", "session-105:tool-round:2");
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
        var hook = new RecordingHook();
        var llmMiddlewareCalls = 0;
        var llmMiddleware = new DelegateLlmCallMiddleware(async (_, next) =>
        {
            llmMiddlewareCalls++;
            await next();
        });
        var loop = new ToolCallLoop(
            tools,
            hooks: new AgentHookPipeline([hook]),
            toolMiddlewares: [],
            llmMiddlewares: [llmMiddleware]);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        result.Should().BeNull();
        messages.Count(m => m.Role == "assistant" && m.ToolCalls?.Count == 1).Should().Be(1);
        messages.Should().ContainSingle(m => m.Role == "tool");
        // Final call should have been made without tools
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Tools.Should().BeNull();
        llmMiddlewareCalls.Should().Be(2);
        hook.LlmStartCount.Should().Be(2);
        hook.LlmEndCount.Should().Be(2);
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

    [Fact]
    public async Task ExecuteAsync_WhenToolMiddlewareTerminates_ShouldUseMiddlewareResultWithoutExecutingTool()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-blocked",
                        Name = "echo",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);

        var toolExecutions = 0;
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", args =>
        {
            toolExecutions++;
            return args;
        }));

        var terminateMiddleware = new DelegateToolCallMiddleware((context, _) =>
        {
            context.Terminate = true;
            context.Result = "blocked-by-middleware";
            return Task.CompletedTask;
        });

        var loop = new ToolCallLoop(
            tools,
            hooks: null,
            toolMiddlewares: [terminateMiddleware],
            llmMiddlewares: []);

        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("done");
        toolExecutions.Should().Be(0);
        messages.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "tc-blocked" && m.Content == "blocked-by-middleware");
    }

    [Fact]
    public async Task ExecuteAsync_WhenLlmMiddlewareTerminates_ShouldReturnMiddlewareResponseWithoutCallingProvider()
    {
        var provider = new QueueLLMProvider([]);
        var middleware = new DelegateLlmCallMiddleware((context, _) =>
        {
            context.Terminate = true;
            context.Response = new LLMResponse { Content = "middleware-answer" };
            return Task.CompletedTask;
        });
        var hook = new RecordingHook();
        var loop = new ToolCallLoop(
            new ToolManager(),
            hooks: new AgentHookPipeline([hook]),
            toolMiddlewares: [],
            llmMiddlewares: [middleware]);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        result.Should().Be("middleware-answer");
        provider.Requests.Should().BeEmpty();
        messages.Should().ContainSingle(m => m.Role == "assistant" && m.Content == "middleware-answer");
        hook.LlmStartCount.Should().Be(1);
        hook.LlmEndCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFinishReasonLength_ShouldInjectNudgeAndConcatenateContent()
    {
        // First response: truncated (finish_reason = "length", no tool calls)
        // Second response: normal completion after continuation nudge
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "partial answer...", FinishReason = "length" },
            new LLMResponse { Content = "...continued and done" },
        ]);
        var loop = new ToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("do something complex") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 5, CancellationToken.None);

        // The returned result should be the full concatenated content, not just the tail.
        result.Should().Be("partial answer......continued and done");
        provider.Requests.Should().HaveCount(2, "should have retried after length truncation");
        // Individual partial messages are preserved in history for the LLM context
        messages.Should().Contain(m => m.Role == "assistant" && m.Content == "partial answer...");
        messages.Should().Contain(m => m.Role == "user" && m.Content!.Contains("cut off due to length"));
        // Final concatenated message in history
        messages.Last(m => m.Role == "assistant").Content.Should().Be("partial answer......continued and done");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFinishReasonLength_ShouldRespectMaxRecoveries()
    {
        // All responses are truncated — should stop after MaxLengthRecoveries (3) attempts
        var responses = Enumerable.Range(0, 5)
            .Select(i => new LLMResponse { Content = $"part-{i}|", FinishReason = "length" })
            .ToList();
        // Add a final-call-without-tools response for when maxRounds is exhausted
        responses.Add(new LLMResponse { Content = "forced-final" });

        var provider = new QueueLLMProvider(responses);
        var loop = new ToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("never ending") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 10, CancellationToken.None);

        // 1 initial + 3 recoveries = 4 calls, then on the 4th truncation it exits
        provider.Requests.Should().HaveCount(4);
        // All 4 partial segments concatenated
        result.Should().Be("part-0|part-1|part-2|part-3|");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFinishReasonMaxTokens_ShouldAlsoRecover()
    {
        // Some providers use "max_tokens" instead of "length"
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "cut off", FinishReason = "max_tokens" },
            new LLMResponse { Content = " completed" },
        ]);
        var loop = new ToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 5, CancellationToken.None);

        result.Should().Be("cut off completed");
        provider.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void IsLengthTruncated_ShouldDetectKnownReasons_CaseInsensitive()
    {
        // Lowercase (direct string values)
        ToolCallLoop.IsLengthTruncated("length").Should().BeTrue();
        ToolCallLoop.IsLengthTruncated("max_tokens").Should().BeTrue();
        // PascalCase (from provider enum .ToString(), e.g. Tornado)
        ToolCallLoop.IsLengthTruncated("Length").Should().BeTrue();
        ToolCallLoop.IsLengthTruncated("Max_Tokens").Should().BeTrue();
        // Non-truncation reasons
        ToolCallLoop.IsLengthTruncated("stop").Should().BeFalse();
        ToolCallLoop.IsLengthTruncated("Stop").Should().BeFalse();
        ToolCallLoop.IsLengthTruncated(null).Should().BeFalse();
        ToolCallLoop.IsLengthTruncated("").Should().BeFalse();
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("data")]
    public async Task ExecuteAsync_WhenToolReturnsLegacyRootImageAliases_ShouldPreserveImageContentParts(string payloadKey)
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-image",
                        Name = "image",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("image", _ =>
            $$"""{"{{payloadKey}}":"Zm9v","media_type":"image/png","text":"diagram"}"""));
        var loop = new ToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("done");
        var toolMessage = messages.Single(m => m.Role == "tool" && m.ToolCallId == "tc-image");
        toolMessage.Content.Should().Be("diagram");
        toolMessage.ContentParts.Should().HaveCount(2);
        toolMessage.ContentParts![0].Kind.Should().Be(ContentPartKind.Text);
        toolMessage.ContentParts[0].Text.Should().Be("diagram");
        toolMessage.ContentParts[1].Kind.Should().Be(ContentPartKind.Image);
        toolMessage.ContentParts[1].DataBase64.Should().Be("Zm9v");
        toolMessage.ContentParts[1].MediaType.Should().Be("image/png");

        provider.Requests.Should().HaveCount(2);
        var forwardedToolMessage = provider.Requests[1].Messages.Single(m => m.Role == "tool" && m.ToolCallId == "tc-image");
        forwardedToolMessage.ContentParts.Should().HaveCount(2);
        forwardedToolMessage.ContentParts![1].Kind.Should().Be(ContentPartKind.Image);
        forwardedToolMessage.ContentParts[1].DataBase64.Should().Be("Zm9v");
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

    private sealed class DelegateLlmCallMiddleware(
        Func<LLMCallContext, Func<Task>, Task> handler) : ILLMCallMiddleware
    {
        public Task InvokeAsync(LLMCallContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class CaptureLlmRequestIdentityMiddleware : ILLMCallMiddleware
    {
        public List<string> RequestIds { get; } = [];
        public List<string> CallIds { get; } = [];

        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            if (context.Items.TryGetValue(LLMRequestMetadataKeys.RequestId, out var requestIdObj) &&
                requestIdObj is string requestId)
            {
                RequestIds.Add(requestId);
            }

            if (context.Items.TryGetValue(LLMRequestMetadataKeys.CallId, out var callIdObj) &&
                callIdObj is string callId)
            {
                CallIds.Add(callId);
            }

            await next();
        }
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
