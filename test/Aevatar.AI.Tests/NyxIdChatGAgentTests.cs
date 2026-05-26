using System.Reflection;
using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public class NyxIdChatGAgentTests
{
    [Fact]
    public async Task ActivateAsync_ShouldPinNyxIdProviderOnFirstInitialization()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateAgent(provider, "nyxid-chat-init");

        await agent.ActivateAsync();

        agent.RoleName.Should().Be(NyxIdChatServiceDefaults.DisplayName);
        agent.State.ConfigOverrides.Should().NotBeNull();
        agent.State.ConfigOverrides.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
        agent.EffectiveConfig.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
    }

    [Fact]
    public async Task ActivateAsync_ShouldMigrateLegacyBlankProviderToNyxId()
    {
        using var provider = BuildServiceProvider();
        var actorId = "nyxid-chat-migration";

        var legacyAgent = CreateAgent(provider, actorId);
        await legacyAgent.ActivateAsync();
        await legacyAgent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = NyxIdChatServiceDefaults.DisplayName,
            ProviderName = string.Empty,
            Model = "claude-sonnet",
            SystemPrompt = "legacy prompt",
            MaxToolRounds = 7,
        });
        await legacyAgent.DeactivateAsync();

        var migratedAgent = CreateAgent(provider, actorId);
        await migratedAgent.ActivateAsync();

        migratedAgent.State.ConfigOverrides.Should().NotBeNull();
        migratedAgent.State.ConfigOverrides.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
        migratedAgent.State.ConfigOverrides.Model.Should().Be("claude-sonnet");
        migratedAgent.State.ConfigOverrides.MaxToolRounds.Should().Be(7);
        migratedAgent.EffectiveConfig.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
        migratedAgent.EffectiveConfig.Model.Should().Be("claude-sonnet");
        migratedAgent.EffectiveConfig.MaxToolRounds.Should().Be(7);
        migratedAgent.EffectiveConfig.SystemPrompt.Should().NotBe("legacy prompt");
        migratedAgent.EffectiveConfig.SystemPrompt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleChatRequest_ShouldContinueToolLoopAndPublishToolLifecycleEvents()
    {
        // ─── Test fixture constants (single source of truth) ───
        const string round1Text = "Confirmed the connector.";
        const string round2Text = "Telegram Bot connection is ready.";
        const string toolCallId = "catalog-call-1";
        const string toolName = "nyxid_catalog";
        const string toolArgs = """{"action":"show","slug":"telegram-bot"}""";
        const string toolResult = """{"slug":"telegram-bot","provider_type":"api_key"}""";

        using var provider = BuildServiceProvider();
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk { DeltaContent = round1Text },
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = toolCallId,
                            Name = toolName,
                            ArgumentsJson = toolArgs,
                        },
                    },
                ],
                [
                    new LLMStreamChunk { DeltaContent = round2Text },
                ],
            ]);
        var toolSources = new IAgentToolSource[]
        {
            new StaticToolSource(
            [
                new DelegateTool(toolName, _ => toolResult),
            ]),
        };
        var agent = CreateAgent(provider, "nyxid-chat-tool-loop", llmProviderFactory, toolSources);
        var eventPublisher = new RecordingEventPublisher();
        agent.EventPublisher = eventPublisher;

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "Connect the Telegram bot",
            SessionId = "session-tool-loop",
        });

        // ─── LLM round assertions ───

        // Two LLM rounds: initial + continuation after tool result
        llmProviderFactory.StreamRequests.Should().HaveCount(2,
            "tool call in round 1 should trigger a second LLM round");

        // Round-2 messages must carry the tool result from round 1
        llmProviderFactory.StreamRequests[1].Messages.Should().ContainSingle(message =>
            message.Role == "tool" &&
            message.ToolCallId == toolCallId &&
            message.Content == toolResult);

        // ─── Tool lifecycle events ───

        eventPublisher.Published.OfType<ToolCallEvent>()
            .Should()
            .ContainSingle(x =>
                x.CallId == toolCallId &&
                x.ToolName == toolName &&
                x.ArgumentsJson.Contains("telegram-bot"));
        eventPublisher.Published.OfType<ToolResultEvent>()
            .Should()
            .ContainSingle(x =>
                x.CallId == toolCallId &&
                x.Success &&
                x.ResultJson.Contains("telegram-bot"));

        // ─── Streaming content events ───

        // RoleGAgent keeps the core ChatRuntime stream transparent by default; the
        // Lark/NyxId deferred reply path opts into hiding tool-call preamble text.
        llmProviderFactory.StreamRequests[1].Messages.Should().ContainSingle(message =>
            message.Role == "assistant" &&
            message.Content == round1Text &&
            message.ToolCalls != null &&
            message.ToolCalls.Count == 1 &&
            message.ToolCalls[0].Id == toolCallId);
        var deltas = eventPublisher.Published.OfType<TextMessageContentEvent>()
            .Select(x => x.Delta).ToList();
        deltas.Should().ContainInOrder(round1Text, round2Text);

        // ─── Completion event ───

        var endEvent = eventPublisher.Published.OfType<TextMessageEndEvent>()
            .Should().ContainSingle().Subject;
        endEvent.Content.Should().StartWith(round1Text);
        endEvent.Content.Should().EndWith(round2Text);
        var middle = endEvent.Content[round1Text.Length..^round2Text.Length];
        middle.Should().MatchRegex(@"^\s*$",
            "only whitespace separators allowed between round-1 and round-2 text");
    }

    [Fact]
    public async Task ActivateAsync_ShouldUseConfiguredRelayCallbackUrlInSystemPrompt()
    {
        using var provider = BuildServiceProvider();
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk { DeltaContent = "ok" },
                ],
            ]);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-relay-prompt",
            llmProviderFactory,
            relayOptions: new NyxIdRelayOptions
            {
                WebhookBaseUrl = "https://dev.aevatar.local/",
            });

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "relay-prompt-session",
        });

        llmProviderFactory.StreamRequests.Should().ContainSingle();
        var systemPrompt = llmProviderFactory.StreamRequests[0].Messages.First(message => message.Role == "system").Content;
        systemPrompt.Should().Contain("https://dev.aevatar.local/api/webhooks/nyxid-relay");
        systemPrompt.Should().NotContain("https://aevatar-console-backend-api.aevatar.ai/api/webhooks/nyxid-relay");
        systemPrompt.Should().Contain("do not call `lark_messages_reply` or `lark_messages_react` to deliver the answer");
        systemPrompt.Should().Contain("the channel runtime will send it through the Nyx relay reply token");
        systemPrompt.Should().NotContain("call `lark_messages_react` first");
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenForwardedPrefixedActorRegistrationUnavailable_ShouldNotDestroyActor()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var runtime = new RecordingActorRuntime();
        using var provider = BuildServiceProvider(registry, runtime);
        var actorId = $"{NyxIdChatServiceDefaults.ActorIdPrefix}-existing";
        var agent = CreateAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = false,
        }));

        registry.UnregisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentTypeName,
            actorId));
        runtime.DestroyedActors.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenLocalActorRegistrationUnavailable_ShouldDestroyActor()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var runtime = new RecordingActorRuntime();
        using var provider = BuildServiceProvider(registry, runtime);
        const string actorId = "routed-id-without-local-prefix";
        var agent = CreateAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
        }));

        registry.UnregisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentTypeName,
            actorId));
        runtime.DestroyedActors.Should().ContainSingle().Which.Should().Be(actorId);
    }

    [Fact]
    public async Task HandleDeletionCompensationAsync_ShouldRestoreRegistryRegistration()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        var runtime = new RecordingActorRuntime();
        using var provider = BuildServiceProvider(registry, runtime);
        const string actorId = "nyxid-chat-delete-compensation";
        var agent = CreateAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationDeletionCompensationRequested
        {
            ScopeId = "scope-a",
            ActorId = actorId,
            Reason = "history_delete_failed",
        }));

        registry.RegisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentTypeName,
            actorId));
    }

    private static ServiceProvider BuildServiceProvider(
        IGAgentActorRegistryCommandPort? registryCommandPort = null,
        IActorRuntime? actorRuntime = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStoreForTests>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));

        if (registryCommandPort is not null)
            services.AddSingleton(registryCommandPort);

        if (actorRuntime is not null)
            services.AddSingleton(actorRuntime);

        return services.BuildServiceProvider();
    }

    private static NyxIdChatGAgent CreateAgent(
        IServiceProvider provider,
        string actorId,
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        NyxIdRelayOptions? relayOptions = null)
    {
        var agent = new NyxIdChatGAgent(llmProviderFactory, toolSources: toolSources, relayOptions: relayOptions)
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
        return agent;
    }

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
        Propagation = new EnvelopePropagation { CorrelationId = Guid.NewGuid().ToString("N") },
    };

    private sealed class RecordingGAgentActorRegistryCommandPort : IGAgentActorRegistryCommandPort
    {
        public GAgentActorRegistryCommandStage RegisterStage { get; init; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;

        public List<GAgentActorRegistration> RegisteredActors { get; } = [];
        public List<GAgentActorRegistration> UnregisteredActors { get; } = [];

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            RegisteredActors.Add(registration);
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(registration, RegisterStage));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            UnregisteredActors.Add(registration);
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }
    }

    private sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> DestroyedActors { get; } = [];

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(new RecordingActor(id));

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            Task.FromResult<IActor>(new RecordingActor(id ?? Guid.NewGuid().ToString("N")));

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            return Task.FromResult<IActor>(new RecordingActor(id ?? Guid.NewGuid().ToString("N")));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedActors.Add(id);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(true);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new RecordingAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent : IAgent
    {
        public string Id => "recording-agent";
        public Task<string> GetDescriptionAsync() => Task.FromResult("recording-agent");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StreamingToolLoopProviderFactory(
        IReadOnlyList<IReadOnlyList<LLMStreamChunk>> responses)
        : ILLMProviderFactory, ILLMProvider
    {
        private int _streamIndex;

        public string Name => NyxIdChatServiceDefaults.ProviderName;

        public List<LLMRequest> StreamRequests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StreamRequests.Add(request);

            var responseIndex = _streamIndex++;
            foreach (var chunk in responses[responseIndex])
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }

            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
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
        public string ParametersSchema => """{"type":"object"}""";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(execute(argumentsJson));
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];

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
            _ = targetActorId;
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
}
