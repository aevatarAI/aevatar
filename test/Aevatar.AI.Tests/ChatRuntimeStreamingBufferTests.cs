using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ChatRuntimeStreamingBufferTests
{
    [Fact]
    public async Task ChatStreamAsync_WhenBufferIsBounded_ShouldStillStreamAllChunks()
    {
        var provider = new StreamingProvider(["A", "B", "C", "D"]);
        var runtime = CreateRuntime(provider, streamBufferCapacity: 1);

        var output = new StringBuilder();
        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        output.ToString().Should().Be("ABCD");
        provider.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenProviderReturnsToolCallDelta_ShouldSurfaceStructuredChunks()
    {
        var provider = new StreamingProvider(["done"], streamToolCall: new ToolCall
        {
            Id = "tc-1",
            Name = "search",
            ArgumentsJson = "{\"q\":\"aevatar\"}",
        });
        var runtime = CreateRuntime(provider, streamBufferCapacity: 2);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
            chunks.Add(chunk);

        chunks.Should().Contain(x => x.DeltaToolCall != null);
        var toolCall = chunks.First(x => x.DeltaToolCall != null).DeltaToolCall!;
        toolCall.Id.Should().Be("tc-1");
        toolCall.Name.Should().Be("search");
        toolCall.ArgumentsJson.Should().Contain("aevatar");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenToolCallIdAppearsLate_ShouldPromoteToSingleFinalToolCall()
    {
        var provider = new StreamingProvider(
            chunks: ["done"],
            streamToolDeltas:
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = string.Empty,
                        Name = "search",
                        ArgumentsJson = "{\"q\":\"ae",
                    },
                },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-merge",
                        Name = string.Empty,
                        ArgumentsJson = "vatar\"}",
                    },
                },
            ]);
        var captureMiddleware = new CaptureLLMResponseMiddleware();
        var runtime = CreateRuntime(provider, streamBufferCapacity: 2, llmMiddlewares: [captureMiddleware]);

        await foreach (var _ in runtime.ChatStreamAsync("hello"))
        {
        }

        captureMiddleware.LastResponse.Should().NotBeNull();
        var toolCalls = captureMiddleware.LastResponse!.ToolCalls;
        toolCalls.Should().NotBeNull();
        toolCalls!.Should().ContainSingle();
        toolCalls[0].Id.Should().Be("tc-merge");
        toolCalls[0].Name.Should().Be("search");
        toolCalls[0].ArgumentsJson.Should().Be("{\"q\":\"aevatar\"}");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenProviderReturnsReasoningDelta_ShouldSurfaceReasoningChunk()
    {
        var provider = new StreamingProvider(
            chunks: [],
            streamToolDeltas:
            [
                new LLMStreamChunk
                {
                    DeltaReasoningContent = "thinking step",
                },
            ]);
        var runtime = CreateRuntime(provider, streamBufferCapacity: 2);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
            chunks.Add(chunk);

        chunks.Should().Contain(x => x.DeltaReasoningContent == "thinking step");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenStreamReturnsToolCall_ShouldExecuteToolAndContinueWithFollowUpRound()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-follow-up",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"lark\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "tool-finished" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}"));
        var runtime = CreateRuntime(provider, streamBufferCapacity: 2, tools: tools);
        var output = new StringBuilder();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", maxToolRounds: 2))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        output.ToString().Should().Be("tool-finished");
        provider.StreamRequests.Should().HaveCount(2);
        provider.StreamRequests[1].Messages.Any(m =>
            m.Role == "assistant" &&
            m.ToolCalls != null &&
            m.ToolCalls.Count == 1 &&
            m.ToolCalls[0].Id == "tc-follow-up").Should().BeTrue();
        provider.StreamRequests[1].Messages.Any(m =>
            m.Role == "tool" &&
            m.ToolCallId == "tc-follow-up" &&
            m.Content == "RESULT:{\"q\":\"lark\"}").Should().BeTrue();
    }

    [Fact]
    public async Task ChatStreamAsync_WhenFinalRoundParsesTextToolCall_ShouldIncludeToolResultInSummaryRequest()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-initial",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"initial\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = """
                        <function_calls>
                        <invoke name="lookup">
                        <parameter name="q">final</parameter>
                        </invoke>
                        </function_calls>
                        """,
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "summary-ready" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}"));
        var runtime = CreateRuntime(provider, streamBufferCapacity: 2, tools: tools);

        var output = new StringBuilder();
        await foreach (var chunk in runtime.ChatStreamAsync("hello", maxToolRounds: 1))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        output.ToString().Should().Contain("summary-ready");
        provider.StreamRequests.Should().HaveCount(3);
        provider.StreamRequests[2].Messages.Any(m =>
            m.Role == "tool" &&
            m.Content == "RESULT:{\"q\":\"final\"}").Should().BeTrue();
    }

    [Fact]
    public async Task ChatStreamAsync_WhenRequestIdentityProvided_ShouldForwardRequestIdAndMergeMetadata()
    {
        var provider = new StreamingProvider(["A"]);
        var runtime = CreateRuntime(
            provider,
            streamBufferCapacity: 2,
            requestBuilder: () => new LLMRequest
            {
                Messages = [],
                RequestId = "base-request",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["base"] = "1",
                    ["override"] = "old",
                },
            });

        var providerMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["override"] = "new",
            ["workflow.run_id"] = "run-1",
        };

        await foreach (var _ in runtime.ChatStreamAsync("hello", "session-42", providerMetadata))
        {
        }

        provider.LastStreamRequest.Should().NotBeNull();
        provider.LastStreamRequest!.RequestId.Should().Be("session-42");
        provider.LastStreamRequest.Metadata.Should().NotBeNull();
        provider.LastStreamRequest.Metadata!["base"].Should().Be("1");
        provider.LastStreamRequest.Metadata["override"].Should().Be("new");
        provider.LastStreamRequest.Metadata["workflow.run_id"].Should().Be("run-1");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenRequestIdentityProvided_ShouldExposeRequestIdToLlmMiddlewareMetadata()
    {
        var provider = new StreamingProvider(["A"]);
        var captureMiddleware = new CaptureLLMMetadataMiddleware();
        var runtime = CreateRuntime(
            provider,
            streamBufferCapacity: 2,
            llmMiddlewares: [captureMiddleware]);

        await foreach (var _ in runtime.ChatStreamAsync("hello", "session-77"))
        {
        }

        captureMiddleware.RequestIds.Should().ContainSingle().Which.Should().Be("session-77");
    }

    [Fact]
    public async Task ChatAsync_WhenAgentMiddlewareTerminates_ShouldReturnSyntheticResultWithoutCallingProvider()
    {
        var provider = new StreamingProvider(["ignored"]);
        var runtime = CreateRuntime(
            provider,
            streamBufferCapacity: 2,
            agentMiddlewares:
            [
                new DelegateAgentRunMiddleware((context, _) =>
                {
                    context.Result = "short-circuit";
                    context.Terminate = true;
                    return Task.CompletedTask;
                }),
            ]);

        var result = await runtime.ChatAsync("hello");

        result.Should().Be("short-circuit");
        provider.ChatCallCount.Should().Be(0);
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenAgentMiddlewareTerminates_ShouldEmitSyntheticContentChunk()
    {
        var provider = new StreamingProvider(["ignored"]);
        var runtime = CreateRuntime(
            provider,
            streamBufferCapacity: 2,
            agentMiddlewares:
            [
                new DelegateAgentRunMiddleware((context, _) =>
                {
                    context.Result = "agent-short-circuit";
                    context.Terminate = true;
                    return Task.CompletedTask;
                }),
            ]);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
            chunks.Add(chunk);

        chunks.Should().ContainSingle();
        chunks[0].DeltaContent.Should().Be("agent-short-circuit");
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenLlmMiddlewareTerminates_ShouldEmitSyntheticContentAndToolCallChunks()
    {
        var provider = new StreamingProvider(["ignored"]);
        var runtime = CreateRuntime(
            provider,
            streamBufferCapacity: 2,
            llmMiddlewares:
            [
                new DelegateLlmCallMiddleware((context, _) =>
                {
                    context.Terminate = true;
                    context.Response = new LLMResponse
                    {
                        Content = "middleware-content",
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "tool-1",
                                Name = "search",
                                ArgumentsJson = "{\"q\":\"aevatar\"}",
                            },
                        ],
                    };
                    return Task.CompletedTask;
                }),
            ]);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
            chunks.Add(chunk);

        chunks.Should().Contain(x => x.DeltaContent == "middleware-content");
        chunks.Should().Contain(x => x.DeltaToolCall != null && x.DeltaToolCall.Id == "tool-1");
        chunks.Should().Contain(x => x.IsLast);
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenProviderEmitsEmptyNonTerminalChunk_ShouldFilterItOut()
    {
        var provider = new StreamingProvider(
            chunks: [],
            streamToolDeltas:
            [
                new LLMStreamChunk(),
            ]);
        var runtime = CreateRuntime(provider, streamBufferCapacity: 2);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
            chunks.Add(chunk);

        chunks.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WhenStreamBufferCapacityIsInvalid_ShouldThrow()
    {
        var provider = new StreamingProvider([]);

        var act = () => CreateRuntime(provider, streamBufferCapacity: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static ChatRuntime CreateRuntime(
        ILLMProvider provider,
        int streamBufferCapacity,
        ToolManager? tools = null,
        IReadOnlyList<IAgentRunMiddleware>? agentMiddlewares = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        Func<LLMRequest>? requestBuilder = null)
    {
        var history = new ChatHistory();
        var toolLoop = new ToolCallLoop(tools ?? new ToolManager());

        return new ChatRuntime(
            providerFactory: () => provider,
            history: history,
            toolLoop: toolLoop,
            hooks: null,
            requestBuilder: requestBuilder ?? (() => new LLMRequest { Messages = [] }),
            agentMiddlewares: agentMiddlewares,
            llmMiddlewares: llmMiddlewares,
            streamBufferCapacity: streamBufferCapacity);
    }

    private sealed class QueuedStreamingProvider(
        IReadOnlyList<IReadOnlyList<LLMStreamChunk>> rounds) : ILLMProvider
    {
        private readonly Queue<IReadOnlyList<LLMStreamChunk>> _rounds = new(rounds);

        public string Name => "queued-streaming-provider";
        public List<LLMRequest> StreamRequests { get; } = [];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse());
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamRequests.Add(request);
            var round = _rounds.Count > 0 ? _rounds.Dequeue() : [];

            foreach (var chunk in round)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class StreamingProvider(
        IReadOnlyList<string> chunks,
        ToolCall? streamToolCall = null,
        IReadOnlyList<LLMStreamChunk>? streamToolDeltas = null) : ILLMProvider
    {
        public string Name => "streaming-provider";
        public int ChatCallCount { get; private set; }
        public int StreamCallCount { get; private set; }
        public LLMRequest? LastStreamRequest { get; private set; }
        public LLMRequest? LastChatRequest { get; private set; }

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            LastChatRequest = request;
            ChatCallCount++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new LLMResponse { Content = string.Concat(chunks) });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastStreamRequest = request;
            StreamCallCount++;

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaContent = chunk };
                await Task.Yield();
            }

            if (streamToolDeltas is { Count: > 0 })
            {
                foreach (var streamChunk in streamToolDeltas)
                {
                    yield return streamChunk;
                    await Task.Yield();
                }
            }
            else if (streamToolCall != null)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = streamToolCall,
                };
            }
        }
    }

    private sealed class CaptureLLMResponseMiddleware : ILLMCallMiddleware
    {
        public LLMResponse? LastResponse { get; private set; }

        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            await next();
            LastResponse = context.Response;
        }
    }

    private sealed class CaptureLLMMetadataMiddleware : ILLMCallMiddleware
    {
        public List<string> RequestIds { get; } = [];

        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            if (context.Items.TryGetValue(LLMRequestMetadataKeys.RequestId, out var requestIdObj) &&
                requestIdObj is string requestId)
            {
                RequestIds.Add(requestId);
            }

            await next();
        }
    }

    private sealed class DelegateAgentRunMiddleware(
        Func<AgentRunContext, Func<Task>, Task> handler) : IAgentRunMiddleware
    {
        public Task InvokeAsync(AgentRunContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class DelegateLlmCallMiddleware(
        Func<LLMCallContext, Func<Task>, Task> handler) : ILLMCallMiddleware
    {
        public Task InvokeAsync(LLMCallContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class DelegateTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => "delegate";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }
}
