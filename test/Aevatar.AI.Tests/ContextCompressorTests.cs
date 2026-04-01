using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class ContextCompressorTests
{
    // ═══════════════════════════════════════════════════════════
    // TokenBudgetTracker
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void RecordUsage_ShouldAccumulateCorrectly()
    {
        var tracker = new TokenBudgetTracker();

        tracker.RecordUsage(new TokenUsage(100, 50, 150));
        tracker.RecordUsage(new TokenUsage(200, 80, 280));
        tracker.RecordUsage(new TokenUsage(300, 120, 420));

        tracker.CallCount.Should().Be(3);
        tracker.LastPromptTokens.Should().Be(300);
        tracker.LastCompletionTokens.Should().Be(120);
        tracker.CumulativePromptTokens.Should().Be(600);
        tracker.CumulativeCompletionTokens.Should().Be(250);
    }

    [Fact]
    public void RecordUsage_WhenNull_ShouldBeNoOp()
    {
        var tracker = new TokenBudgetTracker();
        tracker.RecordUsage(null);

        tracker.CallCount.Should().Be(0);
        tracker.LastPromptTokens.Should().Be(0);
    }

    [Fact]
    public void IsOverBudget_WhenZeroCalls_ShouldReturnFalse()
    {
        var tracker = new TokenBudgetTracker();
        tracker.IsOverBudget(1000).Should().BeFalse();
    }

    [Fact]
    public void IsOverBudget_WhenBudgetDisabled_ShouldReturnFalse()
    {
        var tracker = new TokenBudgetTracker();
        tracker.RecordUsage(new TokenUsage(9999, 0, 9999));
        tracker.IsOverBudget(0).Should().BeFalse();
    }

    [Fact]
    public void IsOverBudget_WhenUnderThreshold_ShouldReturnFalse()
    {
        var tracker = new TokenBudgetTracker();
        tracker.RecordUsage(new TokenUsage(800, 0, 800));
        // 1000 * 0.85 = 850, 800 < 850
        tracker.IsOverBudget(1000, 0.85).Should().BeFalse();
    }

    [Fact]
    public void IsOverBudget_WhenOverThreshold_ShouldReturnTrue()
    {
        var tracker = new TokenBudgetTracker();
        tracker.RecordUsage(new TokenUsage(900, 0, 900));
        // 1000 * 0.85 = 850, 900 > 850
        tracker.IsOverBudget(1000, 0.85).Should().BeTrue();
    }

    [Fact]
    public void Reset_ShouldClearAllState()
    {
        var tracker = new TokenBudgetTracker();
        tracker.RecordUsage(new TokenUsage(100, 50, 150));
        tracker.Reset();

        tracker.CallCount.Should().Be(0);
        tracker.LastPromptTokens.Should().Be(0);
        tracker.LastCompletionTokens.Should().Be(0);
        tracker.CumulativePromptTokens.Should().Be(0);
        tracker.CumulativeCompletionTokens.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // CompactToolResults
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CompactToolResults_ShouldDeduplicateIdenticalContent()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("query"),
            ChatMessage.Tool("tc-1", "same content"),
            ChatMessage.Tool("tc-2", "same content"),
            ChatMessage.Tool("tc-3", "same content"),
        };

        var modified = ContextCompressor.CompactToolResults(messages);

        modified.Should().Be(2);
        messages[1].Content.Should().Be("same content");
        messages[2].Content.Should().Contain("duplicate of tool call tc-1");
        messages[3].Content.Should().Contain("duplicate of tool call tc-1");
    }

    [Fact]
    public void CompactToolResults_ShouldTruncateOversizedResults()
    {
        var longContent = new string('x', 10000);
        var messages = new List<ChatMessage>
        {
            ChatMessage.Tool("tc-1", longContent),
        };

        var modified = ContextCompressor.CompactToolResults(messages, maxToolResultLength: 4000);

        modified.Should().Be(1);
        messages[0].Content!.Length.Should().BeLessThan(longContent.Length);
        messages[0].Content.Should().EndWith("...[compressed]");
    }

    [Fact]
    public void CompactToolResults_ShouldPreserveUniqueResults()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.Tool("tc-1", "result A"),
            ChatMessage.Tool("tc-2", "result B"),
            ChatMessage.Tool("tc-3", "result C"),
        };

        var modified = ContextCompressor.CompactToolResults(messages);

        modified.Should().Be(0);
        messages[0].Content.Should().Be("result A");
        messages[1].Content.Should().Be("result B");
        messages[2].Content.Should().Be("result C");
    }

    [Fact]
    public void CompactToolResults_ShouldHandleEmptyList()
    {
        var messages = new List<ChatMessage>();
        ContextCompressor.CompactToolResults(messages).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // TruncateByImportance
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TruncateByImportance_ShouldPreserveRecentMessages()
    {
        var messages = Enumerable.Range(0, 20)
            .Select(i => ChatMessage.User($"msg-{i}"))
            .ToList();

        ContextCompressor.TruncateByImportance(messages, targetCount: 10, preserveRecentCount: 6);

        messages.Count.Should().BeLessThanOrEqualTo(10);
        // Last 6 should be preserved
        messages.Should().Contain(m => m.Content == "msg-19");
        messages.Should().Contain(m => m.Content == "msg-18");
        messages.Should().Contain(m => m.Content == "msg-17");
        messages.Should().Contain(m => m.Content == "msg-16");
        messages.Should().Contain(m => m.Content == "msg-15");
        messages.Should().Contain(m => m.Content == "msg-14");
    }

    [Fact]
    public void TruncateByImportance_ShouldNeverRemoveSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("system prompt"),
            ChatMessage.User("msg-1"),
            ChatMessage.User("msg-2"),
            ChatMessage.User("msg-3"),
            ChatMessage.User("msg-4"),
            ChatMessage.User("msg-5"),
            ChatMessage.User("msg-6"),
            ChatMessage.User("msg-7"),
        };

        ContextCompressor.TruncateByImportance(messages, targetCount: 4, preserveRecentCount: 2);

        messages.Should().Contain(m => m.Role == "system" && m.Content == "system prompt");
    }

    [Fact]
    public void TruncateByImportance_ShouldPreserveToolCallResultPairs()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("query"),
            ChatMessage.User("old msg 1"),
            ChatMessage.User("old msg 2"),
            ChatMessage.User("old msg 3"),
            new()
            {
                Role = "assistant",
                ToolCalls = [new ToolCall { Id = "tc-1", Name = "search", ArgumentsJson = "{}" }],
            },
            ChatMessage.Tool("tc-1", "tool result"),
            ChatMessage.User("followup"),
            ChatMessage.Assistant("answer"),
        };

        // Target count leaves room for the tool pair, with recent messages protected
        ContextCompressor.TruncateByImportance(messages, targetCount: 5, preserveRecentCount: 2);

        // The tool_call assistant message and tool result should survive together
        var hasAssistantToolCall = messages.Any(m => m.Role == "assistant" && m.ToolCalls?.Count > 0);
        var hasToolResult = messages.Any(m => m.Role == "tool" && m.ToolCallId == "tc-1");

        if (hasAssistantToolCall)
            hasToolResult.Should().BeTrue("tool result should be preserved with its tool_call");
    }

    [Fact]
    public void TruncateByImportance_ShouldNoOpWhenUnderTarget()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("msg-1"),
            ChatMessage.User("msg-2"),
            ChatMessage.User("msg-3"),
        };

        var removed = ContextCompressor.TruncateByImportance(messages, targetCount: 10);

        removed.Should().Be(0);
        messages.Should().HaveCount(3);
    }

    // ═══════════════════════════════════════════════════════════
    // SummarizeOldestBlockAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task SummarizeOldestBlock_ShouldReplaceBlockWithSummary()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("system prompt"),
        };
        for (var i = 0; i < 15; i++)
            messages.Add(ChatMessage.User($"message {i}"));

        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "This is a summary of the conversation." },
        ]);

        var result = await ContextCompressor.SummarizeOldestBlockAsync(
            messages, provider, blockSize: 8);

        result.Should().BeTrue();
        // System prompt should still be first
        messages[0].Role.Should().Be("system");
        messages[0].Content.Should().Be("system prompt");
        // Summary should be second
        messages[1].Role.Should().Be("system");
        messages[1].Content.Should().Contain("[Previous conversation summary]");
        messages[1].Content.Should().Contain("This is a summary of the conversation.");
        // Total count = 1 (system) + 1 (summary) + 7 (remaining) = 9
        messages.Should().HaveCount(9);
    }

    [Fact]
    public async Task SummarizeOldestBlock_ShouldReturnFalseWhenTooFewMessages()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("system prompt"),
            ChatMessage.User("msg 1"),
            ChatMessage.User("msg 2"),
        };

        var provider = new QueueLLMProvider([]);
        var result = await ContextCompressor.SummarizeOldestBlockAsync(
            messages, provider, blockSize: 8);

        result.Should().BeFalse();
        messages.Should().HaveCount(3);
    }

    [Fact]
    public async Task SummarizeOldestBlock_ShouldPreserveMessageOrder()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 16; i++)
            messages.Add(ChatMessage.User($"msg-{i}"));

        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "Summary" },
        ]);

        await ContextCompressor.SummarizeOldestBlockAsync(messages, provider, blockSize: 8);

        // After summarization: 1 (summary) + 8 (remaining) = 9
        messages.Should().HaveCount(9);
        messages[0].Content.Should().Contain("[Previous conversation summary]");
        // Remaining messages should maintain relative order
        for (var i = 1; i < messages.Count; i++)
            messages[i].Content.Should().Be($"msg-{i + 7}");
    }

    // ═══════════════════════════════════════════════════════════
    // Integration: ToolCallLoop records TokenUsage
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ToolCallLoop_ShouldRecordTokenUsage()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "ok", Usage = new TokenUsage(500, 100, 600) },
        ]);
        var tracker = new TokenBudgetTracker();
        var loop = new ToolCallLoop(new ToolManager(), budgetTracker: tracker);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        tracker.LastPromptTokens.Should().Be(500);
        tracker.LastCompletionTokens.Should().Be(100);
        tracker.CallCount.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    // Integration: Compact Hooks Fire
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task CompactHooks_ShouldFireDuringCompression()
    {
        var hook = new CompactRecordingHook();
        var pipeline = new AgentHookPipeline([hook]);
        var history = new ChatHistory { MaxMessages = 100 };

        // Record high token usage to trigger compression
        history.Budget.RecordUsage(new TokenUsage(9000, 0, 9000));

        // Fill history with messages
        for (var i = 0; i < 20; i++)
            history.Add(ChatMessage.User($"msg {i}"));

        var provider = new QueueLLMProvider([new LLMResponse { Content = "ok" }]);
        var toolLoop = new ToolCallLoop(new ToolManager(), pipeline, budgetTracker: history.Budget);
        var compressionConfig = new ContextCompressionConfig(
            MaxPromptTokenBudget: 10000,
            CompressionThreshold: 0.85,
            EnableSummarization: false);
        var chat = new ChatRuntime(
            providerFactory: () => provider,
            history: history,
            toolLoop: toolLoop,
            hooks: pipeline,
            requestBuilder: () => new LLMRequest
            {
                Messages = history.BuildMessages(null),
                Tools = null,
            },
            compressionConfig: compressionConfig);

        await chat.ChatAsync("trigger", maxToolRounds: 1, ct: CancellationToken.None);

        hook.CompactStartCount.Should().Be(1);
        hook.CompactEndCount.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    // Test Doubles
    // ═══════════════════════════════════════════════════════════

    private sealed class QueueLLMProvider : ILLMProvider
    {
        private readonly Queue<LLMResponse> _responses;

        public QueueLLMProvider(IEnumerable<LLMResponse> responses) =>
            _responses = new Queue<LLMResponse>(responses);

        public string Name => "queue";

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LLMResponse());
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CompactRecordingHook : IAIGAgentExecutionHook
    {
        public string Name => "compact_recorder";
        public int Priority => 0;

        public int CompactStartCount { get; private set; }
        public int CompactEndCount { get; private set; }

        public Task OnCompactStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            CompactStartCount++;
            return Task.CompletedTask;
        }

        public Task OnCompactEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            CompactEndCount++;
            return Task.CompletedTask;
        }
    }
}
