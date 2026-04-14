using System.IO;
using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatbotClassifier;
using FluentAssertions;
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
    public async Task HandleChatRequest_ShouldPublishClassifierResponseFromChatAsync()
    {
        const string responseJson =
            """{"intent":"faq","intent_type":"faq","reply":"Here is the answer.","context_summary":"faq","params":{}}""";

        using var provider = AgentCoverageTestSupport.BuildServiceProvider();
        var agent = CreateAgent(
            provider,
            "chatbot-classifier-success",
            new StubChatProviderFactory((request, ct) =>
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
    public async Task HandleChatRequest_ShouldEmitFallbackJsonWhenChatAsyncFails()
    {
        using var provider = AgentCoverageTestSupport.BuildServiceProvider();
        var agent = CreateAgent(
            provider,
            "chatbot-classifier-failure",
            new StubChatProviderFactory((_, _) => throw new InvalidOperationException("synthetic failure")));
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
        ILLMProviderFactory? llmProviderFactory = null)
    {
        var agent = new ChatbotClassifierGAgent(llmProviderFactory)
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        AgentCoverageTestSupport.AssignActorId(agent, actorId);
        return agent;
    }
}
