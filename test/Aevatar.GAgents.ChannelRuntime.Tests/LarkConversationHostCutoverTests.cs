using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkConversationHostCutoverTests
{
    [Fact]
    public async Task Ingress_DailyReportIntent_ShouldRouteThroughConversationGAgent()
    {
        var registration = BuildRegistration();
        var registrationQueryPort = BuildRegistrationQueryPort(registration);
        var runtime = new HybridActorRuntime();
        var streams = new InMemoryStreamProvider();
        var eventStore = new InMemoryEventStore();
        var outbound = new RecordingPlatformAdapter("lark");

        await using var services = await BuildServicesAsync(
            runtime,
            streams,
            eventStore,
            outbound,
            registrationQueryPort,
            nyxClient: BuildDefaultNyxClient(),
            replyGenerator: new StubReplyGenerator("group-reply"));

        var ingress = services.GetRequiredService<ILarkConversationIngressRuntime>();
        var result = await ingress.HandleAsync(
            BuildHttpContext(BuildMessageWebhookJson(
                text: "/daily-report",
                messageId: "om_daily_1",
                chatId: "oc_daily_chat_1",
                chatType: "p2p")),
            registration,
            CancellationToken.None);

        var executed = await ExecuteAsync(result, services);
        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        outbound.Replies.Should().ContainSingle();
        outbound.Replies[0].Inbound.ConversationId.Should().Be("oc_daily_chat_1");
        LarkPlatformAdapter.IsInteractiveCardPayload(outbound.Replies[0].ReplyText).Should().BeTrue();
        outbound.Replies[0].ReplyText.Should().Contain("Create Daily Report Agent");

        runtime.CreatedActorIds.Should().Contain(ConversationGAgent.BuildActorId("lark:dm:ou_123"));
        runtime.CreatedActorIds.Should().NotContain(id => id.StartsWith("channel-user-", StringComparison.Ordinal));
        runtime.GetConversationAgent("lark:dm:ou_123").State.ProcessedMessageIds.Should().ContainSingle("om_daily_1");
        streams.GetStreamRecord(LarkConversationInboxRuntime.InboxStreamId).ProducedActivityIds.Should().ContainSingle("om_daily_1");
    }

    [Fact]
    public async Task Ingress_DailyReportSubmit_ShouldExecuteAgentBuilderAfterRenderedCard()
    {
        var registration = BuildRegistration();
        var registrationQueryPort = BuildRegistrationQueryPort(registration);
        var runtime = new HybridActorRuntime();
        var streams = new InMemoryStreamProvider();
        var eventStore = new InMemoryEventStore();
        var outbound = new RecordingPlatformAdapter("lark");

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");
        runtime.RegisterFactory<SkillRunnerGAgent>(_ => skillRunnerActor);

        var userAgentQueryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        userAgentQueryPort.GetStateVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        userAgentQueryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = call.ArgAt<string>(0),
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
            }));

        var nyxHandler = new RoutingJsonHandler();
        nyxHandler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        nyxHandler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active",
                  "connected_at":"2026-04-15T00:00:00Z"
                }
              ]
            }
            """);
        nyxHandler.Add(HttpMethod.Get, "/api/v1/proxy/services", """
            [
              {"id":"svc-github","slug":"api-github"},
              {"id":"svc-lark","slug":"api-lark-bot"}
            ]
            """);
        nyxHandler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-1","full_key":"full-key-1"}""");

        await using var services = await BuildServicesAsync(
            runtime,
            streams,
            eventStore,
            outbound,
            registrationQueryPort,
            nyxClient: new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(nyxHandler) { BaseAddress = new Uri("https://nyx.example.com") }),
            replyGenerator: new StubReplyGenerator("group-reply"),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(userAgentQueryPort);
            });

        var ingress = services.GetRequiredService<ILarkConversationIngressRuntime>();

        var launchResult = await ingress.HandleAsync(
            BuildHttpContext(BuildMessageWebhookJson(
                text: "/daily-report",
                messageId: "om_daily_launch_1",
                chatId: "oc_daily_chat_1",
                chatType: "p2p")),
            registration,
            CancellationToken.None);

        (await ExecuteAsync(launchResult, services)).StatusCode.Should().Be(StatusCodes.Status200OK);
        outbound.Replies.Should().ContainSingle();
        var cardReply = outbound.Replies[0].ReplyText;
        LarkPlatformAdapter.IsInteractiveCardPayload(cardReply).Should().BeTrue();

        var submitResult = await ingress.HandleAsync(
            BuildHttpContext(BuildCardActionWebhookJson(
                cardJson: cardReply,
                eventId: "evt_daily_submit_1",
                chatId: "oc_daily_chat_1",
                chatType: "p2p",
                formValues: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["github_username"] = "alice",
                    ["repositories"] = "aevatarAI/aevatar",
                    ["schedule_time"] = "09:00",
                    ["schedule_timezone"] = "UTC",
                })),
            registration,
            CancellationToken.None);

        (await ExecuteAsync(submitResult, services)).StatusCode.Should().Be(StatusCodes.Status200OK);
        outbound.Replies.Should().HaveCount(2);
        outbound.Replies[1].ReplyText.Should().Contain("Daily report agent created: skill-runner-");
        outbound.Replies[1].ReplyText.Should().Contain("View Agents");

        runtime.CreatedActorIds.Should().Contain(ConversationGAgent.BuildActorId("lark:dm:ou_123"));
        runtime.CreatedActorIds.Should().NotContain(id => id.StartsWith("channel-user-", StringComparison.Ordinal));
        runtime.GetConversationAgent("lark:dm:ou_123").State.ProcessedMessageIds.Should().Contain("evt_daily_submit_1");

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(envelope =>
                envelope.Payload != null &&
                envelope.Payload.Is(InitializeSkillRunnerCommand.Descriptor) &&
                envelope.Payload.Unpack<InitializeSkillRunnerCommand>().TemplateName == "daily_report" &&
                envelope.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.ConversationId == "oc_daily_chat_1"),
            Arg.Any<CancellationToken>());

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(envelope =>
                envelope.Payload != null &&
                envelope.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor) &&
                envelope.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason == "create_agent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingress_GroupChat_ShouldReplyThroughConversationActor()
    {
        var registration = BuildRegistration();
        var registrationQueryPort = BuildRegistrationQueryPort(registration);
        var runtime = new HybridActorRuntime();
        var streams = new InMemoryStreamProvider();
        var eventStore = new InMemoryEventStore();
        var outbound = new RecordingPlatformAdapter("lark");

        await using var services = await BuildServicesAsync(
            runtime,
            streams,
            eventStore,
            outbound,
            registrationQueryPort,
            nyxClient: BuildDefaultNyxClient(),
            replyGenerator: new StubReplyGenerator("group-echo"));

        var ingress = services.GetRequiredService<ILarkConversationIngressRuntime>();
        var result = await ingress.HandleAsync(
            BuildHttpContext(BuildMessageWebhookJson(
                text: "hello group",
                messageId: "om_group_1",
                chatId: "oc_group_chat_1",
                chatType: "group")),
            registration,
            CancellationToken.None);

        (await ExecuteAsync(result, services)).StatusCode.Should().Be(StatusCodes.Status200OK);
        outbound.Replies.Should().ContainSingle();
        outbound.Replies[0].ReplyText.Should().Be("group-echo");
        outbound.Replies[0].Inbound.ChatType.Should().Be("group");
        outbound.Replies[0].Inbound.ConversationId.Should().Be("oc_group_chat_1");

        runtime.CreatedActorIds.Should().Contain(ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1"));
        runtime.CreatedActorIds.Should().NotContain(id => id.StartsWith("channel-user-", StringComparison.Ordinal));
        runtime.GetConversationAgent("lark:group:oc_group_chat_1").State.ProcessedMessageIds.Should().ContainSingle("om_group_1");
    }

    [Fact]
    public async Task Ingress_DuplicateWebhook_ShouldDeduplicateByProcessedMessageId()
    {
        var registration = BuildRegistration();
        var registrationQueryPort = BuildRegistrationQueryPort(registration);
        var runtime = new HybridActorRuntime();
        var streams = new InMemoryStreamProvider();
        var eventStore = new InMemoryEventStore();
        var outbound = new RecordingPlatformAdapter("lark");

        await using var services = await BuildServicesAsync(
            runtime,
            streams,
            eventStore,
            outbound,
            registrationQueryPort,
            nyxClient: BuildDefaultNyxClient(),
            replyGenerator: new StubReplyGenerator("dedup-once"));

        var ingress = services.GetRequiredService<ILarkConversationIngressRuntime>();
        var payload = BuildMessageWebhookJson(
            text: "hello once",
            messageId: "om_duplicate_1",
            chatId: "oc_dup_chat_1",
            chatType: "p2p");

        (await ExecuteAsync(await ingress.HandleAsync(BuildHttpContext(payload), registration, CancellationToken.None), services))
            .StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ExecuteAsync(await ingress.HandleAsync(BuildHttpContext(payload), registration, CancellationToken.None), services))
            .StatusCode.Should().Be(StatusCodes.Status200OK);

        outbound.Replies.Should().ContainSingle();
        streams.GetStreamRecord(LarkConversationInboxRuntime.InboxStreamId).ProducedActivityIds
            .Should().Equal("om_duplicate_1", "om_duplicate_1");

        var conversation = runtime.GetConversationAgent("lark:dm:ou_123");
        conversation.State.ProcessedMessageIds.Should().ContainSingle("om_duplicate_1");
        (await eventStore.GetEventsAsync(ConversationGAgent.BuildActorId("lark:dm:ou_123"))).Should().HaveCount(1);
    }

    private static ChannelBotRegistrationEntry BuildRegistration() => new()
    {
        Id = "reg-1",
        Platform = "lark",
        NyxProviderSlug = "api-lark-bot",
        NyxUserToken = "org-token",
        VerificationToken = "verify-token",
        ScopeId = "scope-1",
        CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static IChannelBotRegistrationQueryPort BuildRegistrationQueryPort(ChannelBotRegistrationEntry registration)
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync(registration.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));
        queryPort.GetStateVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(1));
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([registration]));
        return queryPort;
    }

    private static NyxIdApiClient BuildDefaultNyxClient() =>
        new(new NyxIdToolOptions { BaseUrl = "https://example.com" });

    private static async Task<ServiceProvider> BuildServicesAsync(
        HybridActorRuntime runtime,
        InMemoryStreamProvider streamProvider,
        InMemoryEventStore eventStore,
        RecordingPlatformAdapter outbound,
        IChannelBotRegistrationQueryPort registrationQueryPort,
        NyxIdApiClient nyxClient,
        IConversationReplyGenerator replyGenerator,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient(LarkConversationHostDefaults.HttpClientName, client =>
        {
            client.BaseAddress = LarkConversationHostDefaults.BaseAddress;
        });
        services.AddSingleton<IEventStore>(eventStore);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<IActorRuntime>(runtime);
        services.AddSingleton<IStreamProvider>(streamProvider);
        services.AddSingleton<IPlatformAdapter>(outbound);
        services.AddSingleton(registrationQueryPort);
        services.AddSingleton(nyxClient);
        services.AddSingleton(replyGenerator);
        services.AddSingleton<IChannelRuntimeDiagnostics, InMemoryChannelRuntimeDiagnostics>();
        services.AddSingleton<LarkMessageComposer>();
        services.AddSingleton<LarkPayloadRedactor>();
        services.AddSingleton<LarkConversationAdapterRegistry>();
        services.AddSingleton<ConversationDispatchMiddleware>();
        services.AddSingleton<ConversationResolverMiddleware>();
        services.AddSingleton<LoggingMiddleware>();
        services.AddSingleton<TracingMiddleware>();
        services.AddSingleton(_ => new MiddlewarePipelineBuilder()
            .Use<TracingMiddleware>()
            .Use<LoggingMiddleware>()
            .Use<ConversationResolverMiddleware>()
            .Use<ConversationDispatchMiddleware>());
        services.AddSingleton<ChannelPipeline>(sp => sp.GetRequiredService<MiddlewarePipelineBuilder>().Build(sp));
        services.AddSingleton<IConversationTurnRunner, LarkConversationTurnRunner>();
        services.AddSingleton<LarkConversationInboxRuntime>();
        services.AddSingleton<ILarkConversationInbox>(sp => sp.GetRequiredService<LarkConversationInboxRuntime>());
        services.AddSingleton<ILarkConversationIngressRuntime, LarkConversationIngressRuntime>();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        runtime.AttachServices(provider);
        await provider.GetRequiredService<LarkConversationInboxRuntime>().StartAsync(CancellationToken.None);
        return provider;
    }

    private static DefaultHttpContext BuildHttpContext(string payloadJson)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payloadJson));
        context.Request.ContentType = "application/json";
        return context;
    }

    private static async Task<(int StatusCode, string Body)> ExecuteAsync(IResult result, IServiceProvider services)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static string BuildMessageWebhookJson(
        string text,
        string messageId,
        string chatId,
        string chatType) =>
        JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
                token = "verify-token",
            },
            @event = new
            {
                sender = new
                {
                    sender_id = new
                    {
                        open_id = "ou_123",
                    },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = chatId,
                    message_id = messageId,
                    message_type = "text",
                    chat_type = chatType,
                    content = JsonSerializer.Serialize(new
                    {
                        text,
                    }),
                    create_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                },
            },
        });

    private static string BuildCardActionWebhookJson(
        string cardJson,
        string eventId,
        string chatId,
        string chatType,
        IReadOnlyDictionary<string, string> formValues)
    {
        var actionValue = ExtractPrimaryFormActionValue(cardJson);
        return JsonSerializer.Serialize(new
        {
            schema = "2.0",
            header = new
            {
                event_type = "card.action.trigger",
                token = "verify-token",
                event_id = eventId,
            },
            @event = new
            {
                @operator = new
                {
                    open_id = "ou_123",
                },
                context = new
                {
                    open_chat_id = chatId,
                    open_message_id = "om_card_msg_1",
                    chat_type = chatType,
                },
                action = new
                {
                    value = actionValue,
                    form_value = formValues,
                },
            },
        });
    }

    private static JsonElement ExtractPrimaryFormActionValue(string cardJson)
    {
        using var card = JsonDocument.Parse(cardJson);
        var form = card.RootElement
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .First(element => element.GetProperty("tag").GetString() == "form");
        var action = form
            .GetProperty("elements")
            .EnumerateArray()
            .First(element => element.GetProperty("tag").GetString() == "action");
        return action.GetProperty("actions")[0].GetProperty("value").Clone();
    }

    private sealed class StubReplyGenerator(string reply) : IConversationReplyGenerator
    {
        public Task<string?> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken ct)
        {
            _ = activity;
            _ = metadata;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(reply);
        }
    }

    private sealed class RecordingPlatformAdapter(string platform) : IPlatformAdapter
    {
        public string Platform { get; } = platform;

        public List<ReplyRecord> Replies { get; } = [];

        public Task<IResult?> TryHandleVerificationAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<IResult?>(null);

        public Task<InboundMessage?> ParseInboundAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<InboundMessage?>(null);

        public Task<PlatformReplyDeliveryResult> SendReplyAsync(
            string replyText,
            InboundMessage inbound,
            ChannelBotRegistrationEntry registration,
            NyxIdApiClient nyxClient,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Replies.Add(new ReplyRecord(replyText, inbound, registration));
            return Task.FromResult(new PlatformReplyDeliveryResult(true, "ok"));
        }
    }

    private sealed record ReplyRecord(
        string ReplyText,
        InboundMessage Inbound,
        ChannelBotRegistrationEntry Registration);

    private sealed class HybridActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        private readonly Dictionary<System.Type, Func<string, IActor>> _factories = new();
        private IServiceProvider? _services;

        public List<string> CreatedActorIds { get; } = [];

        public void AttachServices(IServiceProvider services)
        {
            _services = services;
        }

        public void RegisterFactory<TAgent>(Func<string, IActor> factory) where TAgent : IAgent
        {
            _factories[typeof(TAgent)] = factory;
        }

        public ConversationGAgent GetConversationAgent(string canonicalKey)
        {
            var actorId = ConversationGAgent.BuildActorId(canonicalKey);
            return ((ConversationActor)_actors[actorId]).Instance;
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            if (_actors.TryGetValue(actorId, out var existing))
                return Task.FromResult(existing);

            IActor actor;
            if (agentType == typeof(ConversationGAgent))
            {
                actor = ConversationActor.Create(actorId, _services ?? throw new InvalidOperationException("Services not attached."));
            }
            else if (_factories.TryGetValue(agentType, out var factory))
            {
                actor = factory(actorId);
            }
            else
            {
                throw new InvalidOperationException($"No factory registered for actor type '{agentType.FullName}'.");
            }

            _actors[actorId] = actor;
            CreatedActorIds.Add(actorId);
            return Task.FromResult(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private sealed class ConversationActor : IActor
        {
            public ConversationActor(string id, ConversationGAgent instance)
            {
                Id = id;
                Instance = instance;
            }

            public string Id { get; }

            public IAgent Agent => Instance;

            public ConversationGAgent Instance { get; }

            public static ConversationActor Create(string actorId, IServiceProvider services)
            {
                var agent = new ConversationGAgent
                {
                    Services = services,
                    Logger = NullLogger.Instance,
                    EventSourcingBehaviorFactory =
                        services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
                };
                SetAgentId(agent, actorId);
                agent.ActivateAsync().GetAwaiter().GetResult();
                return new ConversationActor(actorId, agent);
            }

            public Task ActivateAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task DeactivateAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (envelope.Payload?.Is(ChatActivity.Descriptor) == true)
                {
                    await Instance.HandleInboundActivityAsync(envelope.Payload.Unpack<ChatActivity>());
                    return;
                }

                if (envelope.Payload?.Is(ConversationContinueRequestedEvent.Descriptor) == true)
                {
                    await Instance.HandleContinueCommandAsync(envelope.Payload.Unpack<ConversationContinueRequestedEvent>());
                    return;
                }

                throw new InvalidOperationException("Unsupported conversation payload.");
            }

            public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

            public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
                Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class InMemoryStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, InMemoryStream> _streams = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId) => GetStreamRecord(actorId);

        public InMemoryStream GetStreamRecord(string actorId)
        {
            if (_streams.TryGetValue(actorId, out var stream))
                return stream;

            stream = new InMemoryStream(actorId);
            _streams[actorId] = stream;
            return stream;
        }
    }

    private sealed class InMemoryStream(string streamId) : IStream
    {
        private readonly List<IStreamSubscription> _subscriptions = [];

        public string StreamId { get; } = streamId;

        public List<string> ProducedActivityIds { get; } = [];

        public async Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (message is ChatActivity activity)
                ProducedActivityIds.Add(activity.Id);

            foreach (var subscription in _subscriptions.ToArray())
                await subscription.DeliverAsync(message, ct);
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            ct.ThrowIfCancellationRequested();
            var subscription = new StreamSubscription<T>(handler, _subscriptions);
            _subscriptions.Add(subscription);
            return Task.FromResult<IAsyncDisposable>(subscription);
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }
    }

    private interface IStreamSubscription : IAsyncDisposable
    {
        Task DeliverAsync(IMessage message, CancellationToken ct);
    }

    private sealed class StreamSubscription<TMessage> : IStreamSubscription
        where TMessage : IMessage, new()
    {
        private readonly Func<TMessage, Task> _handler;
        private readonly List<IStreamSubscription> _owner;
        private readonly MessageDescriptor _descriptor = new TMessage().Descriptor;

        public StreamSubscription(Func<TMessage, Task> handler, List<IStreamSubscription> owner)
        {
            _handler = handler;
            _owner = owner;
        }

        public Task DeliverAsync(IMessage message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (message is TMessage typed)
                return _handler(typed);

            if (message is EventEnvelope envelope && envelope.Payload != null && envelope.Payload.Is(_descriptor))
                return _handler(envelope.Payload.Unpack<TMessage>());

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new InvalidOperationException($"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(evt => evt.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { appended.Select(evt => evt.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(evt => evt.Version > fromVersion.Value).Select(evt => evt.Clone()).ToList()
                : stream.Select(evt => evt.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(evt => evt.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);

        public void Add(HttpMethod method, string path, string body)
        {
            _responses[$"{method.Method} {path}"] = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = $"{request.Method.Method} {request.RequestUri?.PathAndQuery ?? string.Empty}";
            if (!_responses.TryGetValue(key, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"error":"not_found"}"""),
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static void SetAgentId(object agent, string id)
    {
        var type = agent.GetType();
        var prop = type.GetProperty("Id");
        var setter = prop?.GetSetMethod(nonPublic: true);
        if (setter is not null)
        {
            setter.Invoke(agent, [id]);
            return;
        }

        while (type is not null)
        {
            var setIdMethod = type.GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
            if (setIdMethod is not null)
            {
                setIdMethod.Invoke(agent, [id]);
                return;
            }

            type = type.BaseType!;
        }

        throw new InvalidOperationException("Unable to set actor id.");
    }
}
