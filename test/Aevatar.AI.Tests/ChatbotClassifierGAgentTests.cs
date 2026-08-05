using System.IO;
using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatbotClassifier;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class ChatbotClassifierGAgentTests
{
    [Fact]
    public async Task ActivateAsync_ShouldInitializeClassifierPromptAndDisableToolRounds()
    {
        using var provider = AgentCoverageTestSupport.BuildServiceProvider();
        var agent = CreateAgent(provider, "chatbot-classifier-activate");

        await agent.ActivateAsync();

        agent.RoleName.Should().Be("NyxID Chatbot Classifier");
        agent.State.ConfigOverrides.SystemPrompt.Should().NotBeNullOrWhiteSpace();
        agent.State.ConfigOverrides.MaxToolRounds.Should().Be(0);
        agent.EffectiveConfig.SystemPrompt.Should().Contain("JSON");
    }

    [Fact]
    public async Task HandleChatRequest_ShouldPublishClassifierResponseFromStreamAggregation()
    {
        const string responseJson =
            """{"intent":"faq","intent_type":"faq","reply":"Here is the answer.","context_summary":"faq","params":{}}""";

        using var provider = AgentCoverageTestSupport.BuildServiceProvider();
        var agent = CreateAgent(
            provider,
            "chatbot-classifier-success",
            new StubStreamingProviderFactory((request, ct) =>
            {
                request.Messages.Should().NotBeEmpty();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new LLMResponse { Content = responseJson });
            }));
        var publisher = new TestRecordingEventPublisher();
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "How do I connect Telegram?",
            SessionId = "classifier-success",
            Metadata = { { "scope", "scope-a" } },
        });

        publisher.Published.OfType<TextMessageStartEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "classifier-success" && x.AgentId == "chatbot-classifier-success");
        publisher.Published.OfType<TextMessageContentEvent>()
            .Should()
            .ContainSingle(x => x.Delta == responseJson && x.SessionId == "classifier-success");
        publisher.Published.OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x => x.Content == responseJson && x.SessionId == "classifier-success");
    }

    [Fact]
    public async Task HandleChatRequest_ShouldEmitFallbackJsonWhenChatStreamAsyncFails()
    {
        using var provider = AgentCoverageTestSupport.BuildServiceProvider();
        var agent = CreateAgent(
            provider,
            "chatbot-classifier-failure",
            new StubStreamingProviderFactory((_, _) => throw new InvalidOperationException("synthetic failure")));
        var publisher = new TestRecordingEventPublisher();
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "classifier-failure",
        });

        var content = publisher.Published.OfType<TextMessageContentEvent>().Should().ContainSingle().Subject;
        content.Delta.Should().Contain("intent\":\"unknown");
        content.Delta.Should().Contain("Sorry, I'm having trouble right now");
        publisher.Published.OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x => x.Content == content.Delta);
    }

    [Fact]
    public async Task HostDeadline_ShouldCommitTimeoutAndReleaseClassifierForNextMessage()
    {
        const string actorId = "chatbot-classifier-deadline";
        const string timedOutSessionId = "classifier-timeout";
        using var services = AgentCoverageTestSupport.BuildServiceProvider();
        var timeProvider = new ManualDeadlineTimeProvider();
        var llmProvider = new HangingThenSuccessfulProviderFactory();
        var agent = CreateAgent(
            services,
            actorId,
            llmProvider,
            timeProvider,
            new RoleChatExecutionOptions(1_000));
        var publisher = new TestRecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        var timedOutTurn = agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "classify hanging request",
            SessionId = timedOutSessionId,
            TimeoutMs = 0,
        });
        await llmProvider.FirstStreamStarted;
        timeProvider.Advance(TimeSpan.FromMilliseconds(1_000));
        await timedOutTurn;

        var completion = (await services.GetRequiredService<IEventStore>().GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Where(completed => completed.SessionId == timedOutSessionId)
            .Should().ContainSingle().Which;
        completion.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        completion.FailureCode.Should().Be("LLM_TIMEOUT");
        completion.Content.Should().NotContain("intent\":\"unknown");
        publisher.Published.OfType<TextMessageContentEvent>()
            .Should().NotContain(content => content.SessionId == timedOutSessionId &&
                                          content.Delta.Contains("intent\":\"unknown", StringComparison.Ordinal));

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "classify next request",
            SessionId = "classifier-next",
        });

        llmProvider.StreamCallCount.Should().Be(2);
        agent.State.Sessions["classifier-next"].Completed.Should().BeTrue();
        agent.State.Sessions["classifier-next"].FinalContent.Should().Contain("intent\":\"faq");
    }

    [Fact]
    public void AddChatbotClassifier_ShouldReturnSameCollection_AndLoadEmbeddedPrompt()
    {
        var services = new ServiceCollection();

        services.AddChatbotClassifier().Should().BeSameAs(services);

        var prompt = AgentCoverageTestSupport.GetStaticProperty<string>(
            typeof(ChatbotClassifierGAgent).Assembly,
            "Aevatar.GAgents.ChatbotClassifier.ChatbotClassifierSystemPrompt",
            "Value");
        prompt.Should().NotBeNullOrWhiteSpace();
        prompt.Should().Contain("JSON");
    }

    private static ChatbotClassifierGAgent CreateAgent(
        IServiceProvider provider,
        string actorId,
        ILLMProviderFactory? llmProviderFactory = null,
        TimeProvider? timeProvider = null,
        RoleChatExecutionOptions? chatExecutionOptions = null)
    {
        var agent = new ChatbotClassifierGAgent(
            TestAgentToolExecutionPort.Instance,
            llmProviderFactory,
            timeProvider: timeProvider,
            chatExecutionOptions: chatExecutionOptions)
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        AgentCoverageTestSupport.AssignActorId(agent, actorId);
        return agent;
    }

    private sealed class StubStreamingProviderFactory(
        Func<LLMRequest, CancellationToken, Task<LLMResponse>> onChatStreamAsync)
        : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "test-provider";

        public ILLMProvider GetProvider(string name)
        {
            _ = name;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await onChatStreamAsync(request, ct);
            if (!string.IsNullOrEmpty(response.Content))
                yield return new LLMStreamChunk { DeltaContent = response.Content };

            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = response.Usage,
                FinishReason = response.FinishReason,
            };
        }
    }

    private sealed class HangingThenSuccessfulProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        private readonly TaskCompletionSource _firstStreamStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _streamCallCount;

        public string Name => "classifier-hanging-then-successful";
        public Task FirstStreamStarted => _firstStreamStarted.Task;
        public int StreamCallCount => _streamCallCount;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            if (Interlocked.Increment(ref _streamCallCount) == 1)
            {
                _firstStreamStarted.TrySetResult();
                await _neverCompletes.Task.WaitAsync(ct);
                yield break;
            }

            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk
            {
                DeltaContent =
                    """{"intent":"faq","intent_type":"faq","reply":"next","context_summary":"next","params":{}}""",
            };
        }
    }
}
