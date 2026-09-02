using System.ClientModel.Primitives;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.LLMProviders.MEAI;
using FluentAssertions;
using Microsoft.Extensions.AI;

using AevatarChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Aevatar.AI.Tests;

// 06-20-observatory-token-graph-ui (R2): the streaming provider used to drop token usage entirely —
// the switch over update.Contents never matched UsageContent and the terminal chunk carried no Usage,
// so every workflow run reported 0 tokens. These cover the two-part fix: (A) opt into streaming usage on
// the OpenAI request, (B) capture the emitted UsageContent and surface it on the terminal chunk.
public sealed class MEAILLMProviderUsageTests
{
    [Fact]
    public async Task ChatStreamAsync_ShouldEmitTokenUsage_FromStreamingUsageContent()
    {
        var client = new UsageCapturingChatClient
        {
            OnGetStreamingResponse = (_, _, _) => StreamWithUsage(
                ["hello"],
                new UsageDetails { InputTokenCount = 12, OutputTokenCount = 8, TotalTokenCount = 20 }),
        };
        var provider = new MEAILLMProvider("meai-usage", client);

        var chunks = await CollectAsync(provider);

        var last = chunks[^1];
        last.IsLast.Should().BeTrue();
        last.Usage.Should().NotBeNull();
        last.Usage!.PromptTokens.Should().Be(12);
        last.Usage.CompletionTokens.Should().Be(8);
        last.Usage.TotalTokens.Should().Be(20);
    }

    [Fact]
    public async Task ChatStreamAsync_ShouldFallBackTotalToPromptPlusCompletion_WhenProviderOmitsTotal()
    {
        var client = new UsageCapturingChatClient
        {
            OnGetStreamingResponse = (_, _, _) => StreamWithUsage(
                ["hi"],
                new UsageDetails { InputTokenCount = 30, OutputTokenCount = 12, TotalTokenCount = null }),
        };
        var provider = new MEAILLMProvider("meai-usage", client);

        var chunks = await CollectAsync(provider);

        chunks[^1].Usage.Should().NotBeNull();
        chunks[^1].Usage!.TotalTokens.Should().Be(42);
    }

    [Fact]
    public async Task ChatStreamAsync_ShouldLeaveUsageNull_WhenProviderReturnsNoUsage()
    {
        var client = new UsageCapturingChatClient
        {
            OnGetStreamingResponse = (_, _, _) => StreamText(["only", " text"]),
        };
        var provider = new MEAILLMProvider("meai-usage", client);

        var chunks = await CollectAsync(provider);

        var last = chunks.Single(chunk => chunk.IsLast);
        last.Usage.Should().BeNull();
        string.Concat(chunks.Where(chunk => chunk.DeltaContent != null).Select(chunk => chunk.DeltaContent))
            .Should().Be("only text");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenStreamingFallbackInvokesTool_ShouldCarryStableToolIdentity()
    {
        var innerClient = new FallbackFunctionCallChatClient();
        var executionPort = new RecordingExecutionPort();
        var provider = new MEAILLMProvider(
            "meai-usage",
            new FunctionInvokingChatClient(innerClient),
            toolExecutionPort: executionPort);
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-alpha", "ambient-round-call"),
            ExecutionOwner = AgentToolExecutionOwners.Actor("actor-alpha"),
        });
        var request = new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
            Model = "gpt-test",
            Tools = [new FakeAgentTool("test_tool")],
        };

        var chunks = new List<LLMStreamChunk>();
        await foreach (var chunk in provider.ChatStreamAsync(request))
            chunks.Add(chunk);

        innerClient.StreamingCalls.Should().Be(1);
        innerClient.ResponseCalls.Should().Be(2);
        chunks.Where(chunk => chunk.DeltaContent != null).Select(chunk => chunk.DeltaContent)
            .Should().ContainSingle().Which.Should().Be("done");
        var executionRequest = executionPort.Requests.Should().ContainSingle().Subject;
        executionRequest.ExecutionContext.Request.RequestId.Should().Be("request-alpha");
        executionRequest.ExecutionContext.Request.CallId.Should().Be("meai-request-alpha-iteration-0-function-0");
        executionRequest.ExecutionContext.Request.CallId.Should().NotBe("ambient-round-call");
        executionRequest.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        executionRequest.ExecutionOwner.OwnerId.Should().Be("actor-alpha");
        executionRequest.ArgumentsJson.Should().Be("{\"city\":\"Paris\"}");
    }

    [Fact]
    public async Task ChatStreamAsync_ShouldOptIntoStreamUsage_ButNonStreamingFallbackShouldNot()
    {
        ChatOptions? streamingOptions = null;
        ChatOptions? fallbackOptions = null;
        var fallbackInvoked = false;
        var client = new UsageCapturingChatClient
        {
            OnGetStreamingResponse = (_, options, _) =>
            {
                streamingOptions = options;
                return EmptyStream();
            },
            OnGetResponse = options =>
            {
                fallbackInvoked = true;
                fallbackOptions = options;
            },
        };
        var provider = new MEAILLMProvider("meai-usage", client);

        // A Model on the request makes BuildOptions return non-null options for both paths so the
        // fallback assertion compares a real options object (not a trivially-null one).
        _ = await CollectAsync(provider, "gpt-test");

        // (A) the streaming request opts into usage via a raw ChatCompletionOptions patch.
        streamingOptions.Should().NotBeNull();
        streamingOptions!.RawRepresentationFactory.Should().NotBeNull();
        var rawOptions = streamingOptions.RawRepresentationFactory!(client);
        rawOptions.Should().NotBeNull();
        var json = ModelReaderWriter.Write(rawOptions!).ToString();
        json.Should().Contain("stream_options");
        json.Should().Contain("include_usage");

        // Scoping: the zero-chunk non-streaming fallback runs and its options must NOT carry the
        // stream-usage opt-in (OpenAI rejects stream_options without stream=true).
        fallbackInvoked.Should().BeTrue();
        fallbackOptions.Should().NotBeNull();
        fallbackOptions!.RawRepresentationFactory.Should().BeNull();
    }

    private static async Task<List<LLMStreamChunk>> CollectAsync(MEAILLMProvider provider, string? model = null)
    {
        var request = new LLMRequest
        {
            Messages = [new AevatarChatMessage { Role = "user", Content = "hi" }],
            Model = model,
        };
        var chunks = new List<LLMStreamChunk>();
        await foreach (var chunk in provider.ChatStreamAsync(request))
            chunks.Add(chunk);
        return chunks;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamText(IEnumerable<string> parts)
    {
        foreach (var part in parts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, part);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithUsage(
        IEnumerable<string> parts,
        UsageDetails usage)
    {
        foreach (var part in parts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, part);
            await Task.Yield();
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new UsageContent(usage) });
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class FallbackFunctionCallChatClient : IChatClient
    {
        public int StreamingCalls { get; private set; }
        public int ResponseCalls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ResponseCalls++;
            if (ResponseCalls == 1)
            {
                return Task.FromResult(new ChatResponse(new MeaiChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("", "test_tool", new Dictionary<string, object?>
                    {
                        ["city"] = "Paris",
                    })])));
            }

            return Task.FromResult(new ChatResponse(new MeaiChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            return EmptyStream();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeAgentTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema =>
            """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""";
        public int ExecutionCalls { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecutionCalls++;
            return Task.FromResult("{}");
        }
    }

    private sealed class RecordingExecutionPort : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            const string resultJson = "{\"ok\":true}";
            return Task.FromResult(new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                resultJson,
                new AgentToolReceipt
                {
                    CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                    ToolName = request.Tool.Name,
                    Status = AgentToolReceiptStatus.Success,
                    ResultJson = resultJson,
                },
                IsMutation: false,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true));
        }
    }

    private sealed class UsageCapturingChatClient : IChatClient
    {
        public Func<IEnumerable<MeaiChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? OnGetStreamingResponse { get; init; }

        public Action<ChatOptions?>? OnGetResponse { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            OnGetResponse?.Invoke(options);
            return Task.FromResult(new ChatResponse(new MeaiChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeaiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            OnGetStreamingResponse?.Invoke(messages, options, cancellationToken) ?? EmptyStream();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
