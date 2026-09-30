using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChannelRuntime.Tests.Identity;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.ExternalCallbacks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed partial class ChannelConversationTurnRunnerTests
{
    [Fact]
    public async Task ConnectLinkTool_ToCommittedOperation_ToStreamedAgentRun_ToOriginalChannel()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var runtime = new CallbackIntegrationInbox();
        var provider = new CallbackIntegrationLLM();
        var external = new CallbackIntegrationNyxID(clock);
        var issuer = new NyxIdConnectLinkVerifierTests.Issuer();
        var registration = BuildNewRegistrationEntry();
        var vault = new InMemorySecretVault(clock);
        var key = await vault.PutAsync(new StoreSecretRequest(CredentialSecretPurposes.ChannelNyxIdAgentKey,
            registration.ScopeId, registration.NyxAgentApiKeyId, "original-registration-agent-key", "callback-integration"));
        registration.ChannelAgentKey.SecretReference = key.Reference.Clone();
        registration.WorkflowResultDeliveryCredential = key.Reference.Clone();
        var outbound = new RecordingJsonHandler("""{"message_id":"original-channel-output"}""");
        using var services = CreateCallbackIntegrationServices(clock, runtime, provider, external, issuer,
            registration, vault, outbound);
        runtime.Services = services;
        var conversation = (ConversationGAgent)(await runtime.CreateAsync<ConversationGAgent>("conversation-original")).Agent;
        var context = CallbackIntegrationContext(registration);
        using var client = services.GetRequiredService<INyxIdApiClientFactory>().CreateClient();
        var tool = new NyxIdConnectLinksTool(client, services.GetRequiredService<IChannelConnectLinkContinuationPort>());
        string accepted;
        using (AgentToolContextScope.Push(context))
            accepted = await tool.ExecuteAsync("""{"service_slug":"google-workspace","label":"My Google","callback_url":"https://ignored.example","target_org_id":"wrong-owner"}""");

        using var acceptedJson = JsonDocument.Parse(accepted);
        var callbackId = acceptedJson.RootElement.GetProperty("callback_id").GetString()!;
        acceptedJson.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        external.Requests.Should().BeEmpty("admission only enqueues the operation; tools never await its commit");
        outbound.Requests.Should().BeEmpty();
        await runtime.DrainAsync();

        var operation = runtime.Agents.OfType<ExternalCallbackGAgent>().Should().ContainSingle().Subject;
        operation.State.Registration.ExternalRequestId.Should().Be("link-exact");
        operation.State.LinkUrl.Should().Be("https://nyx.example/connect/original-link-token");
        operation.State.Result.Should().Be(CallbackResult.Unspecified);
        operation.State.LinkPresented.Should().BeTrue();
        operation.State.Registration.Origin.OriginalActivity.Conversation.CanonicalKey.Should().Be("opaque-original-thread-route");
        operation.State.Registration.Origin.OriginalActivity.Conversation.Partition.Should().Be("thread-original");
        operation.State.Registration.Origin.OriginalActivity.Conversation.Scope.Should().Be(ConversationScope.Thread);
        operation.State.Registration.Authorization.BindingId.Should().Be("binding-original");
        operation.State.Registration.Authorization.OwnerScopeId.Should().Be("sender-owner");
        outbound.Requests.Should().ContainSingle().Which.Body.Should().Contain("original-link-token");
        var projected = await services.GetRequiredService<IExternalCallbackQueryPort>().FindAsync(callbackId, "link-exact");
        projected.Should().NotBeNull();
        projected!.StateVersion.Should().BeGreaterThan(0);
        external.Complete = true;

        var callbackHttp = new DefaultHttpContext
        {
            RequestServices = services,
            Request = { QueryString = new QueryString("?connect_link_id=link-exact&status=completed") },
            Response = { Body = new MemoryStream() },
        };
        var endpoint = await ConnectLinkCallbackEndpoints.HandleAsync(callbackHttp,
            services.GetRequiredService<IExternalCallbackCommandPort>(), CancellationToken.None);
        await endpoint.ExecuteAsync(callbackHttp);
        callbackHttp.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        provider.Requests.Should().BeEmpty("the callback endpoint merely admits a verification hint");
        await runtime.DrainAsync();

        operation.State.Result.Should().Be(CallbackResult.Succeeded);
        operation.State.VerifiedReferences.ConnectedServiceId.Should().Be("instance-exact");
        operation.State.ConsumedAtUnixMs.Should().BeGreaterThan(0);
        conversation.State.ExternalCallbackActions.Should().ContainSingle().Which.ResumeAdmitted.Should().BeTrue();
        var run = runtime.Agents.OfType<AgentRunGAgent>().Should().ContainSingle().Subject;
        run.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff, "the resumed run must finish streaming, error={0}, drop={1}",
            run.State.ErrorCode, run.State.PendingDropNotificationReason);
        run.State.GenerationStep.ToolContext.Channel.Continuation.CanonicalConversationKey.Should().Be("opaque-original-thread-route");
        run.State.GenerationStep.ToolContext.Channel.Continuation.ConversationPartition.Should().Be("thread-original");
        provider.Requests.Should().ContainSingle();
        var prompt = string.Join("\n", provider.Requests[0].Messages.Select(message => message.Content));
        prompt.Should().Contain("Prepare my report after connecting Google");
        prompt.Should().Contain("instance-exact");
        prompt.Should().Contain("google-workspace-7");
        outbound.Requests.Should().HaveCount(2);
        outbound.Requests.Should().OnlyContain(request => request.Authorization == "Bearer original-registration-agent-key");
        outbound.Requests[1].Body.Should().Contain("original-relay-anchor");
        outbound.Requests[1].Body.Should().Contain("Google is connected; the original report action can continue.");
        external.Requests.Should().OnlyContain(request => request.Authorization == "Bearer freshly-minted-original-user");
        external.Requests.Select(request => request.Path).Should().ContainInOrder(
            "/api/v1/connect-links", "/api/v1/connect-links/link-exact", "/api/v1/user-services");

        // A duplicate browser return redelivers protocol receipts without repeating the business turn.
        await services.GetRequiredService<IExternalCallbackCommandPort>().HintAsync(callbackId, "link-exact");
        await runtime.DrainAsync();
        provider.Requests.Should().ContainSingle();
        outbound.Requests.Should().HaveCount(2);
    }

    private static ServiceProvider CreateCallbackIntegrationServices(TimeProvider clock, CallbackIntegrationInbox runtime,
        CallbackIntegrationLLM provider, HttpMessageHandler external, INyxIdConnectedServiceCapabilityIssuer issuer,
        ChannelBotRegistrationEntry registration, ISecretVault vault, RecordingJsonHandler outbound,
        Action<IServiceCollection>? configure = null)
    {
        var documents = new InMemoryProjectionDocumentStore<ExternalCallbackCurrentStateDocument, string>(doc => doc.Id);
        var generator = new NyxIdConversationReplyGenerator(provider,
            new ConversationReplyGeneratorTests.StubBuiltInPromptFloorProvider("Respond to the original business request."));
        var relayOptions = new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { StreamingRepliesEnabled = false };
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IActorRuntime>(runtime).AddSingleton<IActorDispatchPort>(runtime)
            .AddSingleton<IEventStore>(new IdentityGAgentTestHarness.InMemoryEventStore())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IActorRuntimeCallbackScheduler, IdentityGAgentTestHarness.NoopCallbackScheduler>()
            .AddSingleton(vault)
            .AddSingleton<ICommittedStatePublicationHook>(new CallbackIntegrationProjectionFeed(documents))
            .AddSingleton<IExternalCallbackQueryPort>(new ExternalCallbackQueryPort(documents))
            .AddSingleton<IExternalCallbackCommandPort, ExternalCallbackCommandPort>()
            .AddSingleton<INyxIdApiClientFactory>(new NyxIdConnectLinkVerifierTests.ClientFactory(external))
            .AddSingleton(issuer)
            .AddSingleton<IConnectLinkCreationPort, NyxIdConnectLinkCreationAdapter>()
            .AddSingleton<IConnectLinkVerificationPort, NyxIdConnectLinkVerifier>()
            .AddSingleton<IChannelConnectLinkContinuationPort, NyxIdConnectLinkContinuationAdapter>()
            .AddSingleton<IConversationReplyGenerator>(generator)
            .AddSingleton(relayOptions)
            .AddSingleton<IAgentRunReplyGenerationExecutorPort>(sp => new AgentRunReplyGenerationExecutor(runtime,
                generator, null, relayOptions, NullLogger<AgentRunReplyGenerationExecutor>.Instance,
                timeProvider: clock, secretVault: vault, connectedServiceCapabilityIssuer: issuer))
            .AddSingleton<IChannelLlmReplyRunDispatcher>(new AgentRunDispatcher(runtime, runtime,
                NullLogger<AgentRunDispatcher>.Instance, clock))
            .AddSingleton<IConversationTurnRunner>(sp => CreateRunner(BuildRegistrationQueryPort(registration),
                new RecordingPlatformAdapter(), sp, relayHandler: outbound));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static AgentToolExecutionContext CallbackIntegrationContext(ChannelBotRegistrationEntry registration) =>
        AgentToolExecutionContext.Empty with
        {
            Request = AgentToolRequestIdentity.Empty with { CallId = "original-tool-action" },
            Caller = AgentToolCallerContext.Empty with { ScopeId = registration.ScopeId, OwnerScopeId = "sender-owner" },
            SenderBinding = new AgentToolSenderBindingContext("binding-original", "sender-owner", "tenant-original"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext("lark", "tenant-original", "sender-original"),
            Channel = AgentToolChannelContext.Empty with
            {
                BotRegistrationId = registration.Id, RegistrationScopeId = registration.ScopeId, Platform = "lark",
                Continuation = new AgentToolChannelContinuationContext
                {
                    ConversationActorId = "conversation-original", ChannelRegistrationId = registration.Id,
                    CanonicalConversationKey = "opaque-original-thread-route", ConversationPartition = "thread-original",
                    ConversationScope = AgentToolChannelConversationScope.Thread,
                    Platform = "lark", SenderId = "sender-original", OriginalActivityId = "original-activity",
                    OriginalUserText = "Prepare my report after connecting Google", ChannelId = "lark", BotId = registration.Id,
                    NyxAgentApiKeyId = registration.NyxAgentApiKeyId, NyxConversationId = "original-conversation-route",
                    NyxProviderSlug = registration.NyxProviderSlug, ReplyMessageId = "original-relay-anchor",
                    OutboundCorrelationId = "original-relay-correlation",
                    OriginalSenderAuthorization = new AgentToolChannelSenderAuthorization
                    {
                        BindingId = "binding-original", OwnerScopeId = "sender-owner", Platform = "lark",
                        Tenant = "tenant-original", ExternalUserId = "sender-original",
                    },
                },
            },
        };

    /// <summary>Deterministic test inbox: enqueue-only transport, explicitly drained actor turns.</summary>
    private sealed class CallbackIntegrationInbox : IActorRuntime, IActorDispatchPort
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        private readonly Queue<(string ActorId, EventEnvelope Envelope)> _inbox = new();
        public IServiceProvider Services { get; set; } = null!;
        public IEnumerable<IAgent> Agents => _actors.Values.Select(actor => actor.Agent);
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);
        public async Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            id ??= Guid.NewGuid().ToString("N");
            if (_actors.TryGetValue(id, out var existing)) return existing;
            var agent = (GAgentBase)ActivatorUtilities.CreateInstance(Services, agentType);
            agent.Services = Services;
            agent.EventPublisher = new CallbackIntegrationPublisher(this, id);
            SetIntegrationActorId(agent, id);
            switch (agent)
            {
                case ExternalCallbackGAgent operation:
                    operation.EventSourcingBehaviorFactory = Services.GetRequiredService<IEventSourcingBehaviorFactory<ExternalCallbackState>>(); break;
                case ConversationGAgent conversation:
                    conversation.EventSourcingBehaviorFactory = Services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(); break;
                case AgentRunGAgent run:
                    run.EventSourcingBehaviorFactory = Services.GetRequiredService<IEventSourcingBehaviorFactory<AgentRunGAgentState>>(); break;
                case ExternalIdentityBindingGAgent binding:
                    binding.EventSourcingBehaviorFactory = Services.GetRequiredService<IEventSourcingBehaviorFactory<ExternalIdentityBindingState>>(); break;
            }
            var actor = new CallbackIntegrationActor(agent);
            _actors.Add(id, actor);
            await actor.ActivateAsync(ct);
            return actor;
        }
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _inbox.Enqueue((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
        public async Task DrainAsync(Func<EventEnvelope, bool>? stopBefore = null)
        {
            for (var turn = 0; _inbox.TryPeek(out var next); turn++)
            {
                if (stopBefore?.Invoke(next.Envelope) == true) return;
                var delivery = _inbox.Dequeue();
                if (turn > 200) throw new InvalidOperationException("Callback integration inbox did not quiesce.");
                await _actors[delivery.ActorId].HandleEventAsync(delivery.Envelope);
            }
        }
        public Task<IActor?> GetAsync(string id) => Task.FromResult(_actors.GetValueOrDefault(id));
        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));
        public Task DestroyAsync(string id, CancellationToken ct = default) { _actors.Remove(id); return Task.CompletedTask; }
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CallbackIntegrationPublisher(CallbackIntegrationInbox runtime, string publisher) : IEventPublisher
    {
        public async Task PublishAsync<T>(T evt, TopologyAudience audience = TopologyAudience.Children, CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where T : IMessage
        {
            if (audience != TopologyAudience.Self) return;
            await runtime.DispatchAsync(publisher, new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"), Payload = Any.Pack(evt),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisher, TopologyAudience.Self),
            }, ct);
        }
        public async Task SendToAsync<T>(string targetActorId, T evt, CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where T : IMessage =>
            await runtime.DispatchAsync(targetActorId, new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"), Payload = Any.Pack(evt),
                Route = EnvelopeRouteSemantics.CreateDirect(publisher, targetActorId),
            }, ct);
    }

    private sealed class CallbackIntegrationActor(IAgent agent) : IActor
    {
        public string Id => agent.Id;
        public IAgent Agent => agent;
        public Task ActivateAsync(CancellationToken ct = default) => agent.ActivateAsync(ct);
        public Task DeactivateAsync(CancellationToken ct = default) => agent.DeactivateAsync(ct);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => agent.HandleEventAsync(envelope, ct);
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class CallbackIntegrationProjectionFeed(
        InMemoryProjectionDocumentStore<ExternalCallbackCurrentStateDocument, string> documents) : ICommittedStatePublicationHook
    {
        public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            if (context.ActorType != typeof(ExternalCallbackGAgent)) return;
            var projector = new ExternalCallbackCurrentStateProjector(new CallbackIntegrationWriter(documents), new SystemProjectionClock());
            await projector.ProjectAsync(new ExternalCallbackMaterializationContext
                { RootActorId = context.ActorId, ProjectionKind = ExternalCallbackCommittedStateProjectionActivationPlanProvider.ProjectionKind },
                new EventEnvelope { Payload = Any.Pack(context.Published), Route = EnvelopeRouteSemantics.CreateDirect(context.ActorId, context.ActorId) }, ct);
        }
    }

    private sealed class CallbackIntegrationWriter(InMemoryProjectionDocumentStore<ExternalCallbackCurrentStateDocument, string> documents)
        : IProjectionWriteDispatcher<ExternalCallbackCurrentStateDocument>
    {
        public Task<ProjectionWriteResult> UpsertAsync(ExternalCallbackCurrentStateDocument readModel, CancellationToken ct = default) => documents.UpsertAsync(readModel, ct);
        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CallbackIntegrationLLM : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "callback-integration";
        public string Reply { get; init; } = "Google is connected; the original report action can continue.";
        public List<LLMRequest> Requests { get; } = [];
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];
        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var split = Reply.Length / 2;
            yield return new LLMStreamChunk { DeltaContent = Reply[..split] };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { DeltaContent = Reply[split..] };
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class CallbackIntegrationNyxID(TimeProvider clock) : HttpMessageHandler
    {
        public bool Complete { get; set; }
        public List<(string Path, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, request.Headers.Authorization?.ToString()));
            var body = path switch
            {
                "/api/v1/connect-links" => JsonSerializer.Serialize(new { id = "link-exact", connect_url = "https://nyx.example/connect/original-link-token", expires_at = clock.GetUtcNow().AddMinutes(15).ToString("O") }),
                "/api/v1/connect-links/link-exact" when Complete => """{"id":"link-exact","status":"completed","service_slug":"google-workspace","connected_service":{"id":"instance-exact","slug":"google-workspace-7"}}""",
                "/api/v1/connect-links/link-exact" => """{"id":"link-exact","status":"pending","service_slug":"google-workspace"}""",
                "/api/v1/user-services" => """{"services":[{"id":"instance-exact","slug":"google-workspace-7","is_active":true,"credential_source":{"type":"personal"}}]}""",
                _ => throw new InvalidOperationException($"Unexpected NyxID request: {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static void SetIntegrationActorId(IAgent agent, string id)
    {
        for (var type = agent.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic) is not { } method) continue;
            method.Invoke(agent, [id]);
            return;
        }
        throw new InvalidOperationException("Actor identity setter unavailable.");
    }
}
