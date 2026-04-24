using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationReplyGeneratorTests
{
    [Fact]
    public async Task GenerateReplyAsync_UsesConfiguredRelayCallbackUrlInSystemPrompt()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new NyxIdRelayOptions
            {
                WebhookBaseUrl = "https://dev.aevatar.local/",
            });

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-1",
                Conversation = new ConversationReference
                {
                    CanonicalKey = "lark:dm:user-1",
                },
                Content = new MessageContent
                {
                    Text = "hello",
                },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Should().Be("ok");
        providerFactory.Requests.Should().ContainSingle();
        var systemPrompt = providerFactory.Requests[0].Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("https://dev.aevatar.local/api/webhooks/nyxid-relay");
        systemPrompt.Should().NotContain("https://aevatar-console-backend-api.aevatar.ai/api/webhooks/nyxid-relay");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithStreamingSinkAndPlaceholderConfigured_EmitsPlaceholderBeforeFirstDelta()
    {
        // Regression for PR#374 P2 review: the first visible Lark message must fire at the
        // outbound RTT, not at first LLM delta. Without a pre-delta placeholder, a cold-start
        // or tool-call-before-first-token makes the ≤1s target impossible to meet.
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new NyxIdRelayOptions
            {
                StreamingPlaceholderText = "…",
            });
        var sink = new RecordingStreamingSink();

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-placeholder",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            sink,
            CancellationToken.None);

        reply.Should().Be("ok");
        // First emit must be the placeholder, before any LLM delta.
        sink.Emissions.Should().NotBeEmpty();
        sink.Emissions[0].Should().Be("…");
        sink.Emissions.Should().Contain("ok");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithStreamingSinkButEmptyPlaceholderOption_SkipsPlaceholderEmit()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new NyxIdRelayOptions
            {
                StreamingPlaceholderText = string.Empty,
            });
        var sink = new RecordingStreamingSink();

        await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-no-placeholder",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            sink,
            CancellationToken.None);

        sink.Emissions.Should().ContainSingle().And.Contain("ok");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithoutStreamingSink_SkipsPlaceholderEmit()
    {
        var providerFactory = new RecordingProviderFactory();
        var generator = new NyxIdConversationReplyGenerator(
            providerFactory,
            relayOptions: new NyxIdRelayOptions
            {
                StreamingPlaceholderText = "…",
            });

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-no-sink",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-1" },
                Content = new MessageContent { Text = "hello" },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Should().Be("ok");
    }

    private sealed class RecordingStreamingSink : IStreamingReplySink
    {
        public List<string> Emissions { get; } = [];

        public Task OnDeltaAsync(string accumulatedText, CancellationToken ct)
        {
            Emissions.Add(accumulatedText);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "recording";

        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LLMResponse
            {
                Content = "non-streaming path should not be used",
            });

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaContent = "ok",
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                IsLast = true,
            };
        }
    }
}
