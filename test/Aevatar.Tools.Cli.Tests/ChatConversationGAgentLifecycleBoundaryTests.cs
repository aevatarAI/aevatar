using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ChatHistory.DependencyInjection;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChatConversationGAgentLifecycleBoundaryTests
{
    [Fact]
    public void ChatConversationGAgent_ShouldUseNarrowTopologyPort_NotRuntimeServiceLocator()
    {
        // Refactor (iter49/cluster-049-chat-history-index-side-lifecycle):
        //   Old pattern: ChatConversationGAgent resolved IActorRuntime via Services locator and created index actor inline during event handling.
        //   New principle: Index actor addressing/provisioning is a constructor-injected narrow domain port; ChatHistoryIndexGAgent created via topology setup, not inline event handling.
        var constructor = typeof(ChatConversationGAgent).GetConstructors()
            .Should().ContainSingle().Subject;

        constructor.GetParameters()
            .Should().ContainSingle(p => p.ParameterType == typeof(IChatHistoryIndexTopologyPort));

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "agents",
            "Aevatar.GAgents.ChatHistory",
            "ChatConversationGAgent.cs"));

        source.Should().NotContain(nameof(ServiceProviderServiceExtensions.GetRequiredService));
        source.Should().NotContain(nameof(ServiceProviderServiceExtensions.GetService));
        source.Should().NotContain("CreateAsync<ChatHistoryIndexGAgent>");
    }

    [Fact]
    public void ChatConversationGAgent_Constructor_ShouldRequireTopologyPort()
    {
        var port = new DefaultChatHistoryIndexTopologyPort();

        var agent = new ChatConversationGAgent(port);
        var act = () => new ChatConversationGAgent(null!);

        agent.Should().NotBeNull();
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("indexTopologyPort");
    }

    [Theory]
    [InlineData("scope-1", "chat-index-scope-1")]
    [InlineData("tenant/a", "chat-index-tenant/a")]
    public void DefaultChatHistoryIndexTopologyPort_ShouldDeriveIndexActorId(
        string scopeId,
        string expectedActorId)
    {
        var port = new DefaultChatHistoryIndexTopologyPort();

        var actorId = port.GetIndexActorId(scopeId);

        actorId.Should().Be(expectedActorId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultChatHistoryIndexTopologyPort_ShouldRejectMissingScope(string? scopeId)
    {
        var port = new DefaultChatHistoryIndexTopologyPort();

        var act = () => port.GetIndexActorId(scopeId!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddChatHistoryGAgents_ShouldRegisterDefaultTopologyPort()
    {
        var services = new ServiceCollection();

        services.AddChatHistoryGAgents();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IChatHistoryIndexTopologyPort) &&
            descriptor.ImplementationType == typeof(DefaultChatHistoryIndexTopologyPort) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddChatHistoryGAgents_ShouldPreserveCustomTopologyPort()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatHistoryIndexTopologyPort, CustomChatHistoryIndexTopologyPort>();

        services.AddChatHistoryGAgents();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IChatHistoryIndexTopologyPort) &&
            descriptor.ImplementationType == typeof(CustomChatHistoryIndexTopologyPort));
    }

    [Fact]
    public void AddStudioInfrastructure_ShouldIncludeChatHistoryTopologyRegistration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddStudioInfrastructure(configuration);

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IChatHistoryIndexTopologyPort) &&
            descriptor.ImplementationType == typeof(DefaultChatHistoryIndexTopologyPort) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task HandleMessagesReplaced_ShouldForwardUpsertToTopologyIndexActor()
    {
        var publisher = new CapturingEventPublisher();
        var agent = new ChatConversationGAgent(new CustomChatHistoryIndexTopologyPort())
        {
            EventSourcing = new ChatConversationEventSourcing(),
            EventPublisher = publisher,
        };

        var evt = new MessagesReplacedEvent
        {
            ScopeId = "scope-1",
            Meta = new ConversationMetaProto
            {
                Id = "conv-1",
                Title = "Conversation",
                MessageCount = 99,
            },
        };
        evt.Messages.Add(new StoredChatMessageProto
        {
            Id = "msg-1",
            Role = "user",
            Content = "hello",
        });

        await agent.HandleMessagesReplaced(evt);

        publisher.Sent.Should().ContainSingle();
        publisher.Sent[0].TargetActorId.Should().Be("custom-scope-1");
        var forwarded = publisher.Sent[0].Payload.Should()
            .BeOfType<ConversationUpsertedEvent>().Subject;
        forwarded.Meta.Id.Should().Be("conv-1");
        forwarded.Meta.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleConversationDeleted_ShouldForwardRemovalToTopologyIndexActor()
    {
        var publisher = new CapturingEventPublisher();
        var agent = new ChatConversationGAgent(new CustomChatHistoryIndexTopologyPort())
        {
            EventSourcing = new ChatConversationEventSourcing(),
            EventPublisher = publisher,
        };
        await agent.HandleMessagesReplaced(new MessagesReplacedEvent
        {
            ScopeId = "scope-1",
            Meta = new ConversationMetaProto { Id = "conv-1", Title = "Conversation" },
            Messages = { new StoredChatMessageProto { Id = "msg-1", Role = "user" } },
        });
        publisher.Sent.Clear();

        await agent.HandleConversationDeleted(new ConversationDeletedEvent
        {
            ScopeId = "scope-1",
            ConversationId = "conv-1",
        });

        publisher.Sent.Should().ContainSingle();
        publisher.Sent[0].TargetActorId.Should().Be("custom-scope-1");
        var forwarded = publisher.Sent[0].Payload.Should()
            .BeOfType<ConversationRemovedEvent>().Subject;
        forwarded.ConversationId.Should().Be("conv-1");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class CustomChatHistoryIndexTopologyPort : IChatHistoryIndexTopologyPort
    {
        public string GetIndexActorId(string scopeId) => $"custom-{scopeId}";
    }

    private sealed class CapturingEventPublisher : IEventPublisher
    {
        public List<SentEnvelope> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Sent.Add(new SentEnvelope(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record SentEnvelope(string TargetActorId, IMessage Payload);

    private sealed class ChatConversationEventSourcing : IEventSourcingBehavior<ChatConversationState>
    {
        private readonly List<IMessage> _pending = [];

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            CurrentVersion += _pending.Count;
            _pending.Clear();
            return Task.FromResult(new EventStoreCommitResult
            {
                LatestVersion = CurrentVersion,
            });
        }

        public Task PersistSnapshotAsync(ChatConversationState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ChatConversationState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<ChatConversationState?>(null);

        public void DiscardPendingEvents()
        {
            _pending.Clear();
        }

        public ChatConversationState TransitionState(ChatConversationState current, IMessage evt)
        {
            if (evt is MessagesReplacedEvent messagesReplaced)
            {
                var next = new ChatConversationState { Meta = messagesReplaced.Meta?.Clone() };
                next.Messages.AddRange(messagesReplaced.Messages);
                return next;
            }

            return evt is ConversationDeletedEvent
                ? new ChatConversationState()
                : current;
        }
    }
}
