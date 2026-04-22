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
            CancellationToken.None);

        reply.Should().Be("ok");
        providerFactory.Requests.Should().ContainSingle();
        var systemPrompt = providerFactory.Requests[0].Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("https://dev.aevatar.local/api/webhooks/nyxid-relay");
        systemPrompt.Should().NotContain("https://aevatar-console-backend-api.aevatar.ai/api/webhooks/nyxid-relay");
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
