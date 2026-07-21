using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Routing;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class RoleGAgentReplayContractTests
{
    [Fact]
    public async Task InitializeRoleEvent_ShouldPersistAndReplayRoleState()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-init-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-researcher",
            RoleName = "researcher",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "be helpful",
            MaxToolRounds = 4,
            MaxHistoryMessages = 32,
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-init-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-init-replay");
        await agent2.ActivateAsync();

        agent2.RoleId.Should().Be("role-researcher");
        agent2.State.RoleId.Should().Be("role-researcher");
        agent2.RoleName.Should().Be("researcher");
        agent2.State.RoleName.Should().Be("researcher");
        agent2.EffectiveConfig.ProviderName.Should().Be("mock");
        agent2.EffectiveConfig.Model.Should().Be("m1");
        agent2.EffectiveConfig.SystemPrompt.Should().Be("be helpful");
        agent2.EffectiveConfig.MaxToolRounds.Should().Be(4);
        agent2.EffectiveConfig.MaxHistoryMessages.Should().Be(32);
    }

    [Fact]
    public async Task InitializeRoleEvent_ShouldPreserveExplicitZeroTemperature()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-temperature-zero");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            SystemPrompt = "system",
            Temperature = 0,
        });

        agent.EffectiveConfig.Temperature.Should().Be(0);

        var persisted = await store.GetEventsAsync("role-temperature-zero");
        persisted.Should().ContainSingle();
        var evt = persisted.Single().EventData.Unpack<InitializeRoleAgentEvent>();
        evt.HasTemperature.Should().BeTrue();
        evt.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task RoleGAgentFactory_ShouldUseEventSourcedInitializePath()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-factory-replay");
        await agent1.ActivateAsync();
        await RoleGAgentFactory.ApplyInitialization(agent1, new RoleYamlConfig
        {
            Name = "assistant",
            Provider = "mock",
            SystemPrompt = "system",
        }, services);
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync("role-factory-replay");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));

        var agent2 = CreateAgent(services, "role-factory-replay");
        await agent2.ActivateAsync();
        agent2.State.RoleName.Should().Be("assistant");
        agent2.RoleName.Should().Be("assistant");
    }

    [Fact]
    public async Task RoutedModules_ShouldReplayAfterReactivate_WithoutReapplyingOnSessionStateChanges()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("module replay");
        var moduleFactory = new CountingEventModuleFactory();
        var services = BuildServices(store, services =>
        {
            services.AddSingleton<IEventModuleFactory<IEventHandlerContext>>(moduleFactory);
        });

        var agent1 = CreateAgent(services, "role-module-replay", provider);
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
            EventModules = "routable,bypass",
            EventRoutes = "event.type == ChatRequestEvent -> routable",
        });

        agent1.State.EventModules.Should().Be("routable,bypass");
        agent1.State.EventRoutes.Should().Be("event.type == ChatRequestEvent -> routable");
        agent1.GetModules().Should().HaveCount(2);
        agent1.GetModules().Should().ContainSingle(m => m.Name == "routable" && m is RoutedEventModule);
        agent1.GetModules().Should().ContainSingle(m => m.Name == "bypass" && m is CountingBypassModule);
        moduleFactory.TryCreateCallCount.Should().Be(2);

        await agent1.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-module-replay",
        });

        moduleFactory.TryCreateCallCount.Should().Be(2);
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(services, "role-module-replay", provider);
        await agent2.ActivateAsync();

        agent2.State.EventModules.Should().Be("routable,bypass");
        agent2.State.EventRoutes.Should().Be("event.type == ChatRequestEvent -> routable");
        agent2.GetModules().Should().HaveCount(2);
        agent2.GetModules().Should().ContainSingle(m => m.Name == "routable" && m is RoutedEventModule);
        agent2.GetModules().Should().ContainSingle(m => m.Name == "bypass" && m is CountingBypassModule);
        moduleFactory.TryCreateCallCount.Should().Be(4);
    }

    [Fact]
    public async Task InitializeRoleEvent_ShouldInitializeLifecycleModulesAppliedAfterActivation()
    {
        var store = new InMemoryEventStoreForTests();
        var moduleFactory = new CountingEventModuleFactory();
        var services = BuildServices(store, services =>
        {
            services.AddSingleton<IEventModuleFactory<IEventHandlerContext>>(moduleFactory);
        });

        var agent = CreateAgent(services, "role-lifecycle-module");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            SystemPrompt = "system",
            EventModules = "lifecycle",
        });

        var module = agent.GetModules().OfType<CountingLifecycleModule>().Single();
        module.InitializeCallCount.Should().Be(1);
        module.DisposeCallCount.Should().Be(0);

        await agent.DeactivateAsync();

        module.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CompletedSession_ShouldReplayCachedCompletionWithoutCallingProviderAgain()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("cached answer");
        var services = BuildServices(store);

        var terminalPublisher = new RecordingEventPublisher();
        var agent1 = CreateAgent(services, "role-session-replay", provider);
        agent1.EventPublisher = terminalPublisher;
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-assistant",
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent1.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });
        await agent1.DeactivateAsync();

        provider.StreamCallCount.Should().Be(1);
        provider.StreamRequests.Should().ContainSingle();
        provider.StreamRequests[0].RequestId.Should().Be("session-1");
        var persisted = await store.GetEventsAsync("role-session-replay");
        persisted.Should().Contain(x => x.EventType.Contains(nameof(RoleChatSessionStartedEvent), StringComparison.Ordinal));
        persisted.Should().Contain(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));
        persisted
            .Single(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>()
            .RoleId
            .Should()
            .Be("role-assistant");

        var agent2 = CreateAgent(services, "role-session-replay", provider);
        agent2.EventPublisher = terminalPublisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
        });

        provider.StreamCallCount.Should().Be(1);
        terminalPublisher.Published
            .OfType<TextMessageStartEvent>()
            .Should()
            .HaveCount(2)
            .And.OnlyContain(x => x.SessionId == "session-1");
        terminalPublisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .HaveCount(2)
            .And.OnlyContain(x => x.Delta == "cached answer" && x.SessionId == "session-1");
        terminalPublisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .HaveCount(2)
            .And.OnlyContain(x => x.Content == "cached answer" && x.SessionId == "session-1");

        var replayedEvents = await store.GetEventsAsync("role-session-replay");
        var completions = replayedEvents
            .Where(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .ToArray();
        completions.Should()
            .ContainSingle(x =>
                x.SessionId == "session-1" &&
                x.Prompt == "hello" &&
                x.Content == "cached answer");
        completions[0].TerminalTime.Should().NotBeNull();
        var replay = replayedEvents
            .Where(x => x.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .Should()
            .ContainSingle(progress =>
                progress.SessionId == "session-1" &&
                progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Replay)
            .Which;
        replay.Replay.Snapshot.TerminalTime.Should().Be(completions[0].TerminalTime);
        replay.Replay.Snapshot.Content.Should().Be("cached answer");
        agent2.State.Sessions["session-1"].TerminalTime.Should().Be(completions[0].TerminalTime);
    }

    [Fact]
    public async Task Completion_ShouldEmbedTerminalTailInOneCommittedFact()
    {
        var store = new RecordingBatchEventStore();
        var services = BuildServices(store);
        var provider = new CountingLlmProviderFactory("atomic answer");
        var agent = CreateAgent(services, "role-atomic-terminal", provider);
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "turn-atomic-terminal",
        });

        var terminalBatch = store.Appends.Should().ContainSingle(batch =>
            batch.Any(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))).Which;
        var completion = terminalBatch.Should().ContainSingle().Which.EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        var progress = completion.TerminalProgress.ToArray();
        progress.Select(evt => evt.PayloadCase).Should().Equal(
            RoleChatSessionProgressedEvent.PayloadOneofCase.Usage,
            RoleChatSessionProgressedEvent.PayloadOneofCase.TextEnded,
            RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal);
        progress.Select(evt => evt.Sequence).Should().Equal(3, 4, 5);
        agent.State.Sessions[completion.SessionId].LastProgressSequence.Should().Be(5);
    }

    [Fact]
    public async Task CompletedConversationHistory_ShouldBeRestoredAfterActorReactivation()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("answer");
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-history-reactivation", provider);
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent1.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "first prompt",
            SessionId = "turn-first",
        });
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(services, "role-history-reactivation", provider);
        await agent2.ActivateAsync();
        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "second prompt",
            SessionId = "turn-second",
        });

        provider.StreamRequests.Should().HaveCount(2);
        provider.StreamRequests[1].Messages
            .Where(static message => message.Role != "system")
            .Select(static message => (message.Role, message.Content))
            .Should()
            .ContainInOrder(
                ("user", "first prompt"),
                ("assistant", "answer"),
                ("user", "second prompt"));
    }

    [Fact]
    public async Task CompletedSession_WithDifferentPrompt_ShouldCommitTypedConflictWithoutOverwritingReplay()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("first answer");
        var services = BuildServices(store);
        var agent = CreateAgent(services, "role-session-conflict", provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "first prompt",
            SessionId = "turn-client-request-1",
            CommandAttemptId = "cmd-attempt-original",
        });
        var completedProgressSequence = agent.State.Sessions["turn-client-request-1"].LastProgressSequence;

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "different prompt",
            SessionId = "turn-client-request-1",
            CommandAttemptId = "cmd-attempt-rejected",
        });

        provider.StreamCallCount.Should().Be(1);
        agent.State.Sessions["turn-client-request-1"].Prompt.Should().Be("first prompt");
        agent.State.Sessions["turn-client-request-1"].FinalContent.Should().Be("first answer");
        var persisted = await store.GetEventsAsync("role-session-conflict");
        var conflict = persisted
            .Single(x => x.EventType.Contains(nameof(RoleChatCommandAttemptRejectedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatCommandAttemptRejectedEvent>();
        conflict.RequestedSessionId.Should().Be("turn-client-request-1");
        conflict.CommandAttemptId.Should().Be("cmd-attempt-rejected");
        conflict.Reason.Should().Be(RoleChatCommandAttemptRejectionReason.PromptMismatch);
        conflict.SafeMessage.Should().NotContain("first prompt").And.NotContain("different prompt");
        persisted
            .Where(x => x.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .Should()
            .NotContain(progress =>
                progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal &&
                progress.Terminal.FailureCode == "IDEMPOTENCY_CONFLICT");
        agent.State.Sessions["turn-client-request-1"].LastProgressSequence
            .Should().Be(completedProgressSequence);
    }

    [Fact]
    public async Task HandleChatRequest_WhenHandlerFailsOutsideProviderStream_ShouldCommitSafeTypedFailure()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("unused answer");
        var services = BuildServices(store);
        var agent = CreateAgent(services, "role-handler-failure", provider);
        agent.EventPublisher = new ThrowOnceEventPublisher(
            static evt => evt is TextMessageStartEvent,
            new InvalidOperationException("bearer-secret should never leave the actor"));
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "turn-handler-failure",
        });

        provider.StreamCallCount.Should().Be(0);
        var persisted = await store.GetEventsAsync("role-handler-failure");
        var completed = persisted
            .Where(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should()
            .ContainSingle()
            .Which;
        completed.SessionId.Should().Be("turn-handler-failure");
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        completed.FailureCode.Should().Be("CHAT_HANDLER_FAILURE");
        completed.SafeMessage.Should().Be("The chat request failed. Please try again.");
        completed.ToString().Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task AuthorizationRequiredReceipt_ShouldBlockOnlyCurrentTurn_AndAdmitNextTurn()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new AuthorizationThenSuccessLlmProviderFactory();
        var services = BuildServices(store, collection =>
            collection.AddSingleton<IAgentToolSource>(
                new StaticToolSource([new AuthorizationRequiredTool()])));
        var agent = CreateAgent(services, "role-authorization-blocker", provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "read private resource",
            SessionId = "turn-blocked",
        });
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "ordinary follow-up",
            SessionId = "turn-next",
        });

        provider.StreamCallCount.Should().Be(2);
        agent.State.Sessions["turn-blocked"].Outcome.Should().Be(RoleChatSessionOutcome.Blocked);
        agent.State.Sessions["turn-next"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        var completions = (await store.GetEventsAsync("role-authorization-blocker"))
            .Where(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .ToArray();
        var blocked = completions.Should().ContainSingle(evt => evt.SessionId == "turn-blocked").Which;
        blocked.Outcome.Should().Be(RoleChatSessionOutcome.Blocked);
        blocked.AuthorizationRequired.ServiceSlug.Should().Be("api-github");
        blocked.AuthorizationRequired.ReasonCode.Should().Be("NYXID_UNAUTHORIZED");
        blocked.AuthorizationRequired.SafeMessage.Should().Be("Connect or reauthorize api-github to continue.");
        blocked.ToString().Should().NotContain("bearer-secret").And.NotContain("credential");
        completions.Should().ContainSingle(evt =>
            evt.SessionId == "turn-next" && evt.Outcome == RoleChatSessionOutcome.Completed);
    }

    [Fact]
    public async Task CompletionNotification_ShouldReplayCommittedTerminalFactAfterRestart()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("completed output");
        var services = BuildServices(store);
        var failingPublisher = new RecordingEventPublisher { FailSends = true };
        var first = CreateAgent(services, "role-terminal-replay", provider);
        first.EventPublisher = failingPublisher;
        await first.ActivateAsync();
        await first.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-1",
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        var request = new ChatRequestEvent
        {
            Prompt = "complete work",
            SessionId = "session-1",
            RunContext = new RoleChatRunContext
            {
                RunId = "run-1",
                CommandId = "cmd-1",
                CorrelationId = "corr-1",
                CompletionNotificationActorId = "service-run:tenant:svc:run-1",
            },
        };
        await first.HandleChatRequest(request);
        first.State.Sessions["session-1"].CompletionNotificationDispatched.Should().BeFalse();
        var committed = (await store.GetEventsAsync("role-terminal-replay"))
            .Single(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        committed.ActorId.Should().Be("role-terminal-replay");
        committed.RunContext.Should().BeEquivalentTo(request.RunContext);
        committed.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        committed.Content.Should().Be("completed output");
        committed.TerminalTime.Should().NotBeNull();

        var recoveredPublisher = new RecordingEventPublisher();
        var recovered = CreateAgent(services, "role-terminal-replay", provider);
        recovered.EventPublisher = recoveredPublisher;

        await recovered.ActivateAsync();

        provider.StreamCallCount.Should().Be(1);
        var sent = recoveredPublisher.Sends.Should().ContainSingle().Subject;
        sent.TargetActorId.Should().Be("service-run:tenant:svc:run-1");
        sent.Options!.Delivery!.DeduplicationOperationId.Should()
            .Be("role-chat-terminal:run-1:cmd-1");
        var notification = sent.Event.Should().BeOfType<RoleChatSessionCompletedEvent>().Which;
        var expectedNotification = committed.Clone();
        expectedNotification.TerminalProgress.Clear();
        notification.Should().BeEquivalentTo(expectedNotification);
        notification.TerminalProgress.Should().BeEmpty(
            "actor-to-actor completion notification carries final authority, not AGUI presentation tail");
        recovered.State.Sessions["session-1"].CompletionNotificationDispatched.Should().BeTrue();
    }

    [Fact]
    public async Task StartedSessionReplay_ShouldResumeProviderCallAndPersistCompletion()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("resumed answer");
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-session-resume", provider);
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-session-resume",
            [
                StateEventFor(
                    "role-session-resume",
                    2,
                    new RoleChatSessionStartedEvent
                    {
                        SessionId = "session-2",
                        Prompt = "hello again",
                    }),
            ],
            expectedVersion: 1);

        var replayPublisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-session-resume", provider);
        agent2.EventPublisher = replayPublisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello again",
            SessionId = "session-2",
        });

        provider.StreamCallCount.Should().Be(1);
        provider.StreamRequests.Should().ContainSingle();
        provider.StreamRequests[0].RequestId.Should().Be("session-2");
        replayPublisher.Published
            .OfType<TextMessageStartEvent>()
            .Should()
            .Contain(x => x.SessionId == "session-2");
        replayPublisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .Contain(x => x.Content == "resumed answer");

        var persisted = await store.GetEventsAsync("role-session-resume");
        persisted.Should().Contain(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleChatRequest_ShouldCommitCompletionBeforePublishingTerminalFrame()
    {
        var inner = new InMemoryEventStoreForTests();
        var operationLog = new List<string>();
        var store = new RecordingCompletionEventStore(inner, operationLog);
        var provider = new CountingLlmProviderFactory("ordered answer");
        var services = BuildServices(store);

        var publisher = new RecordingEventPublisher(operationLog);
        var agent = CreateAgent(services, "role-completion-order", provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-ordered",
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-completion-order",
        });

        operationLog.Should().ContainInOrder(
            "commit:RoleChatSessionCompletedEvent:session-completion-order",
            "publish:TextMessageEndEvent:session-completion-order");
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-completion-order" &&
                x.Content == "ordered answer");
    }

    [Fact]
    public async Task RoleChatSessions_ShouldRetainOnlyRecentBoundedCache()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("bounded");
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-session-retention", provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        for (var i = 1; i <= 130; i++)
        {
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = $"prompt-{i}",
                SessionId = $"session-{i}",
            });
        }

        agent.State.Sessions.Count.Should().Be(128);
        agent.State.Sessions.ContainsKey("session-1").Should().BeFalse();
        agent.State.Sessions.ContainsKey("session-2").Should().BeFalse();
        agent.State.Sessions.ContainsKey("session-130").Should().BeTrue();
    }

    [Fact]
    public async Task HandleChatRequest_WhenProviderThrowsWithTimeout_ShouldPublishWorkflowFailureMarker()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new ThrowingLlmProviderFactory("throwing-timeout", new InvalidOperationException("  provider exploded  "));
        var services = BuildServices(store);

        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, "role-timeout-failure", provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "role-timeout",
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-timeout-failure",
            TimeoutMs = 1000,
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-timeout-failure" &&
                x.Content == "[[AEVATAR_LLM_ERROR]] provider exploded");

        var completed = (await store.GetEventsAsync("role-timeout-failure"))
            .Single(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        completed.Content.Should().Be("[[AEVATAR_LLM_ERROR]] provider exploded");
        completed.ContentEmitted.Should().BeFalse();
        completed.RoleId.Should().Be("role-timeout");
    }

    [Fact]
    public async Task HandleChatRequest_WhenProviderThrowsWithoutTimeout_ShouldIncludeToolNamesInFailureMessage()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new ThrowingLlmProviderFactory("throwing-tools", new InvalidOperationException("  provider exploded  "));
        var services = BuildServices(store, services =>
        {
            services.AddSingleton<IAgentToolSource>(
                new StaticToolSource(
                [
                    new DelegateTool("dangerous_tool", _ => "{}")
                ]));
        });

        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, "role-tool-failure", provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-tool-failure",
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-tool-failure" &&
                x.Content == "LLM request failed [tools=dangerous_tool]: provider exploded");

        var completed = (await store.GetEventsAsync("role-tool-failure"))
            .Single(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        completed.ContentEmitted.Should().BeFalse();
    }

    [Fact]
    public async Task HandleChatRequest_WithoutSessionId_ShouldSkipSessionPersistence()
    {
        var store = new InMemoryEventStoreForTests();
        var provider = new CountingLlmProviderFactory("stateless answer");
        var services = BuildServices(store);

        var agent = CreateAgent(services, "role-no-session", provider);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello without session",
        });

        var persisted = await store.GetEventsAsync("role-no-session");
        persisted.Should().ContainSingle(x => x.EventType.Contains(nameof(InitializeRoleAgentEvent), StringComparison.Ordinal));
        persisted.Should().NotContain(x => x.EventType.Contains(nameof(RoleChatSessionStartedEvent), StringComparison.Ordinal));
        persisted.Should().NotContain(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedSessionReplay_ShouldEmitReasoningToolCallsAndMedia_WhenContentWasNotStreamed()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-rich-session-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-rich-session-replay",
            [
                StateEventFor(
                    "role-rich-session-replay",
                    2,
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-rich",
                        Prompt = "hello rich",
                        Content = "final answer",
                        ReasoningContent = "because",
                        ContentEmitted = false,
                        ToolCalls =
                        {
                            new ToolCallEvent
                            {
                                CallId = "call-1",
                                ToolName = "lookup",
                                ArgumentsJson = "{\"x\":1}",
                            },
                        },
                        OutputParts =
                        {
                            new ChatContentPart
                            {
                                Kind = ChatContentPartKind.Image,
                                Name = "photo.png",
                            },
                        },
                    }),
            ],
            expectedVersion: 1);

        var replayPublisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-rich-session-replay");
        agent2.EventPublisher = replayPublisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello rich",
            SessionId = "session-rich",
        });

        replayPublisher.Published
            .OfType<TextMessageStartEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich");
        replayPublisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich" && x.Delta == "final answer");
        replayPublisher.Published
            .OfType<TextMessageReasoningEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich" && x.Delta == "because");
        replayPublisher.Published
            .OfType<ToolCallEvent>()
            .Should()
            .ContainSingle(x => x.CallId == "call-1" && x.ToolName == "lookup");
        replayPublisher.Published
            .OfType<MediaContentEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich");
        replayPublisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x => x.SessionId == "session-rich" && x.Content == "final answer");
    }

    [Fact]
    public async Task HandleChatRequest_WhenPersistCompletionFails_ShouldNotPublishTerminalFrames()
    {
        var inner = new InMemoryEventStoreForTests();
        var store = new FailOnCompletionEventStore(inner);
        var provider = new ThrowingLlmProviderFactory(
            "throwing-persist-fail",
            new InvalidOperationException("provider failed before commit"));
        var services = BuildServices(store);

        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, "role-persist-fail", provider);
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = provider.Name,
            SystemPrompt = "system",
        });

        var act = () => agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-persist-fail",
        });

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Simulated persistence failure for session completion.");

        // Refactor (iter164/cluster-001-role-completion):
        //   Old pattern: RoleGAgent published the terminal TextMessageEndEvent before completion commit.
        //   New principle: completion commit failure prevents terminal presentation frames from being published.
        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .BeEmpty();

        var persisted = await inner.GetEventsAsync("role-persist-fail");
        persisted.Should().NotContain(x =>
            x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedSessionReplay_WhenFailureContentWasNotStreamed_ShouldNotPublishDisplayContent()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-failure-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-failure-replay",
            [
                StateEventFor(
                    "role-failure-replay",
                    2,
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-failure-replay",
                        Prompt = "hello",
                        Content = "LLM request failed [tools=none]: upstream",
                        ContentEmitted = false,
                    }),
            ],
            expectedVersion: 1);

        var publisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-failure-replay");
        agent2.EventPublisher = publisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-failure-replay",
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-failure-replay" &&
                x.Content == "LLM request failed [tools=none]: upstream");
    }

    [Fact]
    public async Task CompletedSessionReplay_WhenMarkerFailureContentWasNotStreamed_ShouldNotPublishDisplayContent()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-marker-failure-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-marker-failure-replay",
            [
                StateEventFor(
                    "role-marker-failure-replay",
                    2,
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-marker-failure-replay",
                        Prompt = "hello",
                        Content = "[[AEVATAR_LLM_ERROR]] upstream",
                        ContentEmitted = false,
                    }),
            ],
            expectedVersion: 1);

        var publisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-marker-failure-replay");
        agent2.EventPublisher = publisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-marker-failure-replay",
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .BeEmpty();
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-marker-failure-replay" &&
                x.Content == "[[AEVATAR_LLM_ERROR]] upstream");
    }

    [Fact]
    public async Task PublishMissingDisplayContentAsync_WhenCompletionWasNotEmitted_ShouldPublishContentAndMarkEmitted()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(services, "role-missing-display-content");
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        var replayRecord = CreateSessionReplayRecord("final answer", contentEmitted: false);
        var method = typeof(RoleGAgent).GetMethod(
            "PublishMissingDisplayContentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var task = method!.Invoke(agent, ["session-missing-display", replayRecord])
            .Should()
            .BeAssignableTo<Task>()
            .Subject;
        await task;

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-missing-display" &&
                x.Delta == "final answer");
        GetSessionReplayRecordContentEmitted(task).Should().BeTrue();
    }

    [Fact]
    public async Task HandleChatRequest_WhenReplayHasFinalOnlyContent_ShouldPublishDisplayContentBeforeEnd()
    {
        var store = new InMemoryEventStoreForTests();
        var services = BuildServices(store);

        var agent1 = CreateAgent(services, "role-final-only-replay");
        await agent1.ActivateAsync();
        await agent1.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "counting",
            SystemPrompt = "system",
        });
        await agent1.DeactivateAsync();

        await store.AppendAsync(
            "role-final-only-replay",
            [
                StateEventFor(
                    "role-final-only-replay",
                    2,
                    new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-final-only",
                        Prompt = "hello",
                        Content = "final-only answer",
                        ContentEmitted = false,
                    }),
            ],
            expectedVersion: 1);

        var publisher = new RecordingEventPublisher();
        var agent2 = CreateAgent(services, "role-final-only-replay");
        agent2.EventPublisher = publisher;
        await agent2.ActivateAsync();

        await agent2.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-final-only",
        });

        publisher.Published
            .OfType<TextMessageContentEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-final-only" &&
                x.Delta == "final-only answer");
        publisher.Published
            .OfType<TextMessageEndEvent>()
            .Should()
            .ContainSingle(x =>
                x.SessionId == "session-final-only" &&
                x.Content == "final-only answer");

        var persisted = await store.GetEventsAsync("role-final-only-replay");
        persisted
            .Where(x =>
                x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor) &&
                x.EventData.Unpack<RoleChatSessionCompletedEvent>().SessionId == "session-final-only")
            .Should()
            .ContainSingle();
        persisted
            .Where(x => x.EventData.Is(RoleChatSessionProgressedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionProgressedEvent>())
            .Should()
            .ContainSingle(progress =>
                progress.SessionId == "session-final-only" &&
                progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Replay &&
                progress.Replay.Snapshot.Content == "final-only answer");
    }

    private static IServiceProvider BuildServices(
        InMemoryEventStoreForTests store,
        Action<IServiceCollection>? configure = null) =>
        BuildServices((IEventStore)store, configure);

    private static IServiceProvider BuildServices(
        IEventStore store,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static RoleGAgent CreateAgent(
        IServiceProvider services,
        string actorId,
        ILLMProviderFactory? providerFactory = null)
    {
        var agent = new RoleGAgent(providerFactory, toolSources: services.GetServices<IAgentToolSource>())
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static StateEvent StateEventFor(string agentId, long version, IMessage evt) =>
        new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Version = version,
            EventType = evt.Descriptor.FullName,
            EventData = Any.Pack(evt),
            AgentId = agentId,
        };

    private static void AssignActorId(RoleGAgent agent, string actorId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [actorId]);
    }

    private static object CreateSessionReplayRecord(string content, bool contentEmitted)
    {
        var replayRecordType = typeof(RoleGAgent).GetNestedType(
            "SessionReplayRecord",
            BindingFlags.NonPublic);
        replayRecordType.Should().NotBeNull();

        return Activator.CreateInstance(
            replayRecordType!,
            content,
            string.Empty,
            Array.Empty<ToolCall>(),
            Array.Empty<ContentPart>(),
            Array.Empty<AgentToolReceipt>(),
            Array.Empty<ToolResultEvent>(),
            null, // Usage (added by #1700)
            null, // Model (added by #1700)
            contentEmitted,
            RoleChatSessionOutcome.Completed,
            string.Empty,
            string.Empty,
            null)!;
    }

    private static bool GetSessionReplayRecordContentEmitted(Task task)
    {
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var property = result.GetType().GetProperty("ContentEmitted")!;
        return (bool)property.GetValue(result)!;
    }

    private sealed class RecordingBatchEventStore : IEventStore
    {
        private readonly InMemoryEventStoreForTests _inner = new();

        public List<IReadOnlyList<StateEvent>> Appends { get; } = [];

        public async Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.Select(static evt => evt.Clone()).ToArray();
            var result = await _inner.AppendAsync(agentId, batch, expectedVersion, ct);
            Appends.Add(batch);
            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class RecordingEventPublisher(List<string>? operationLog = null) : IEventPublisher
    {
        public bool FailSends { get; init; }

        public List<IMessage> Published { get; } = [];

        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> Sends { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = direction;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            Published.Add(evt);
            if (evt is TextMessageEndEvent textMessageEnd)
                operationLog?.Add($"publish:TextMessageEndEvent:{textMessageEnd.SessionId}");
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Sends.Add((targetActorId, evt, options));
            if (FailSends)
                throw new InvalidOperationException("simulated completion notification failure");

            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = audience;
            return PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
        }
    }

    private sealed class ThrowOnceEventPublisher(
        Func<IMessage, bool> shouldThrow,
        Exception exception) : IEventPublisher
    {
        private bool _thrown;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (!_thrown && shouldThrow(evt))
            {
                _thrown = true;
                throw exception;
            }

            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null) =>
            PublishAsync(evt, TopologyAudience.Self, ct, sourceEnvelope, options);
    }

    private sealed class CountingLlmProviderFactory(string response) : ILLMProviderFactory, ILLMProvider
    {
        public int StreamCallCount { get; private set; }
        public List<LLMRequest> StreamRequests { get; } = [];

        public string Name => "counting";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamCallCount++;
            StreamRequests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaContent = response,
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = new TokenUsage(1, 1, 2),
            };
        }
    }

    private sealed class AuthorizationThenSuccessLlmProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public int StreamCallCount { get; private set; }
        public string Name => "authorization-then-success";
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamCallCount++;
            if (StreamCallCount == 1)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-auth",
                        Name = "authorization_required_test_tool",
                        ArgumentsJson = "{}",
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk { DeltaContent = "follow-up answer" };
            }

            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class AuthorizationRequiredTool : IAgentTool
    {
        public string Name => "authorization_required_test_tool";
        public string Description => "Returns a typed authorization blocker.";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("""{"error":true,"status":401}""");

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.AuthorizationRequired,
                AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                {
                    ServiceSlug = "api-github",
                    ResourceUri = "/repos/private",
                    ReasonCode = "NYXID_UNAUTHORIZED",
                    SafeMessage = "Connect or reauthorize api-github to continue.",
                },
            };
    }

    private sealed class CountingEventModuleFactory : IEventModuleFactory<IEventHandlerContext>
    {
        public int TryCreateCallCount { get; private set; }

        public bool TryCreate(string name, out IEventModule<IEventHandlerContext>? module)
        {
            TryCreateCallCount++;
            module = name switch
            {
                "routable" => new CountingRoutableModule(),
                "bypass" => new CountingBypassModule(),
                "lifecycle" => new CountingLifecycleModule(),
                _ => null,
            };
            return module != null;
        }
    }

    private sealed class CountingRoutableModule : IEventModule<IEventHandlerContext>
    {
        public string Name => "routable";
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => envelope != null;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingLifecycleModule : ILifecycleAwareEventModule
    {
        public string Name => "lifecycle";
        public int Priority => 0;
        public int InitializeCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public bool CanHandle(EventEnvelope envelope) => envelope != null;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task InitializeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            InitializeCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingBypassModule : IEventModule<IEventHandlerContext>, IRouteBypassModule
    {
        public string Name => "bypass";
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => envelope != null;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingLlmProviderFactory(string name, Exception exception) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => name;

        public ILLMProvider GetProvider(string providerName)
        {
            _ = providerName;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            throw exception;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class DelegateTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => $"{name} test tool";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }

    /// <summary>
    /// Wraps an inner store but throws on appends that contain a
    /// <see cref="RoleChatSessionCompletedEvent"/>, simulating a
    /// persistence failure during session completion.
    /// </summary>
    private sealed class FailOnCompletionEventStore(InMemoryEventStoreForTests inner) : IEventStore
    {
        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var list = events.ToList();
            if (list.Any(e => e.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal)))
                throw new InvalidOperationException("Simulated persistence failure for session completion.");

            return inner.AppendAsync(agentId, list, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class RecordingCompletionEventStore(
        InMemoryEventStoreForTests inner,
        List<string> operationLog) : IEventStore
    {
        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var list = events.ToList();
            var result = inner.AppendAsync(agentId, list, expectedVersion, ct);

            foreach (var evt in list)
            {
                if (!evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
                    continue;

                var completed = evt.EventData.Unpack<RoleChatSessionCompletedEvent>();
                operationLog.Add($"commit:RoleChatSessionCompletedEvent:{completed.SessionId}");
            }

            return result;
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId, long? fromVersion = null, CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }
}
