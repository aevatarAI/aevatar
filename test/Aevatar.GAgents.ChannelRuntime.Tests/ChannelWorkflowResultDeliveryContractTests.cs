using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AGUI.Contracts;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.AevatarInvocation;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Cross-module contract gate for channel workflow result delivery (#2675). Every business-bearing
/// segment uses the production implementation; the local actor network only supplies lifecycle and
/// inbox transport that an Orleans runtime would normally provide.
/// </summary>
public sealed class ChannelWorkflowResultDeliveryContractTests
{
    private const string WorkflowActorId = "workflow-actor-alpha";
    private const string RawAgentKey = "nyxid_ag_full_key_1";

    [Fact]
    public async Task ProvisionedHandle_ShouldFlowThroughReadModelToolContextAndWorkflowTerminalOutbox()
    {
        var secretVault = new InMemorySecretVault();
        var relayHandler = new QueueHandler();
        relayHandler.Enqueue("""{"message_id":"reply-1","platform_message_id":"platform-1"}""");
        var callbackScheduler = new NoopCallbackScheduler();
        using var actorNetwork = new ContractActorNetwork(
            CreateOutboundPort(relayHandler),
            new SecretVaultWorkflowResultDeliveryCredentialResolver(secretVault),
            callbackScheduler);

        var documentStore = new RegistrationDocumentStore();
        var projector = new ChannelBotRegistrationProjector(documentStore, new SystemProjectionClock());
        var projectionHook = new RegistrationProjectionHook(projector);
        using var registrationServices = BuildEventSourcingServices(callbackScheduler, projectionHook);
        var registrationAgent = new ChannelBotRegistrationGAgent
        {
            Services = registrationServices,
            EventSourcingBehaviorFactory =
                registrationServices.GetRequiredService<IEventSourcingBehaviorFactory<ChannelBotRegistrationStoreState>>(),
        };
        SetId(registrationAgent, ChannelBotRegistrationGAgent.WellKnownId);
        await registrationAgent.ActivateAsync();
        actorNetwork.RegisterActivated(ChannelBotRegistrationGAgent.WellKnownId, registrationAgent);

        var provisioningHandler = new QueueHandler();
        provisioningHandler.Enqueue($$$"""{"id":"key-123","full_key":"{{{RawAgentKey}}}"}""");
        provisioningHandler.Enqueue("""{"id":"bot-456","status":"pending_webhook"}""");
        provisioningHandler.Enqueue("""{"id":"route-789","default_agent":true}""");
        provisioningHandler.Enqueue("""{"id":"svc-1"}""");
        var provisioningService = new NyxLarkProvisioningService(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(provisioningHandler)),
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorNetwork, actorNetwork),
            secretVault,
            NullLogger<NyxLarkProvisioningService>.Instance);

        var provisioningResult = await provisioningService.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: "user-token",
                AppId: "cli_a1b2c3",
                AppSecret: "secret-xyz",
                VerificationToken: "verify-123",
                WebhookBaseUrl: "https://aevatar.example.com",
                ScopeId: "scope-1",
                Label: "Ops Bot",
                NyxProviderSlug: "api-lark-bot"),
            CancellationToken.None);

        provisioningResult.Succeeded.Should().BeTrue();
        provisioningResult.WorkflowResultDeliveryEnabled.Should().BeTrue();
        projectionHook.Publications.Should().NotBeEmpty();
        documentStore.Documents.Should().ContainKey(provisioningResult.RegistrationId!);

        var registrationQuery = new ChannelBotRegistrationQueryPort(documentStore);
        var projectedRegistration = await registrationQuery.GetAsync(provisioningResult.RegistrationId!);
        projectedRegistration.Should().NotBeNull();
        projectedRegistration!.WorkflowResultDeliveryCredential.Should().NotBeNull();
        projectedRegistration.NyxAgentApiKeyId.Should().Be("key-123");
        provisioningResult.ToString().Should().NotContain(RawAgentKey);
        registrationAgent.State.ToString().Should().NotContain(RawAgentKey);
        documentStore.Documents.Values.Single().ToString().Should().NotContain(RawAgentKey);

        var turnRunner = CreateTurnRunner(registrationQuery);
        var turn = await turnRunner.RunInboundAsync(
            BuildInboundActivity(provisioningResult.RegistrationId!),
            CancellationToken.None);

        turn.Success.Should().BeTrue();
        turn.LlmReplyRequest.Should().NotBeNull();
        var toolContext = AgentToolExecutionContextMapper.FromPayload(turn.LlmReplyRequest!.ToolContext);
        toolContext.Caller.ScopeId.Should().Be("scope-1");
        toolContext.Channel.BotRegistrationId.Should().Be(provisioningResult.RegistrationId);
        toolContext.Channel.WorkflowResultDeliveryCredential.Should().NotBeNull();
        toolContext.Channel.WorkflowResultDeliveryCredential!.SecretReference
            .Should().Be(projectedRegistration.WorkflowResultDeliveryCredential);
        toolContext.Channel.WorkflowResultDeliveryCredential.SubjectId.Should().Be("key-123");

        var workflowDispatch = new ContractWorkflowDispatchService(actorNetwork);
        var deliveryRegistration = new WorkflowRunBackgroundDeliveryRegistrationPort(
            actorNetwork,
            actorNetwork,
            NullLogger<WorkflowRunBackgroundDeliveryRegistrationPort>.Instance);
        var invocationDispatcher = CreateInvocationDispatcher(
            actorNetwork,
            workflowDispatch,
            deliveryRegistration);

        using var toolContextScope = AgentToolContextScope.Push(toolContext);
        var result = await invocationDispatcher.StartWorkflowForChatRunAsync(
            null,
            $$$"""
            {
              "workflow_id": "wf-main",
              "actor_id": "{{{WorkflowActorId}}}",
              "workflow_yamls": [
                "name: wf-main\nroles: []\nsteps:\n  - id: result\n    type: transform"
              ],
              "inputs": { "prompt": "invoice approved" },
              "wait": "stream"
            }
            """);

        result.ErrorCode.Should().BeEmpty();
        result.Status.Should().Be("streaming");
        result.ActorId.Should().Be(WorkflowActorId);
        workflowDispatch.Command.Should().NotBeNull();
        workflowDispatch.Command!.CompletionNotificationTarget.Should().NotBeNull();

        workflowDispatch.Agent.Should().NotBeNull();
        var workflowAgent = workflowDispatch.Agent!;
        workflowAgent.State.Status.Should().Be("completed");
        workflowAgent.State.FinalOutput.Should().Be("invoice approved");
        workflowAgent.State.PendingTerminalNotification.Should().BeNull();
        workflowAgent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);

        var deliveryAgent = actorNetwork.Agents
            .OfType<WorkflowRunDeliveryGAgent>()
            .Should()
            .ContainSingle()
            .Subject;
        deliveryAgent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        deliveryAgent.State.WorkflowResultDeliveryCredential!.SecretReference
            .Should().Be(projectedRegistration.WorkflowResultDeliveryCredential);

        var reserveIndex = actorNetwork.IndexOf<WorkflowRunDeliveryReserveRequested>();
        var workflowDispatchIndex = actorNetwork.IndexOf<WorkflowChatRequestEvent>();
        var terminalIndex = actorNetwork.IndexOf<WorkflowRunTerminalNotification>();
        var registrationIndex = actorNetwork.IndexOf<WorkflowRunDeliveryStartRequested>();
        reserveIndex.Should().BeGreaterThanOrEqualTo(0);
        workflowDispatchIndex.Should().BeGreaterThan(reserveIndex,
            "delivery must be reserved before workflow dispatch");
        terminalIndex.Should().BeGreaterThan(workflowDispatchIndex);
        registrationIndex.Should().BeGreaterThan(terminalIndex,
            "this contract exercises terminal-before-accepted-receipt binding");

        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        relayHandler.Requests[0].Authorization.Should().Be($"Bearer {RawAgentKey}");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"invoice approved\"");
    }

    [Fact]
    public async Task RepairedHistoricalHandle_ShouldPreserveRegistrationAndDeliverWorkflowTeamResult()
    {
        const string rawRepairedAgentKey = "nyxid_ag_repaired_alpha";
        const string registrationId = "reg-alpha";
        const string requestId = "repair-alpha";
        var secretVault = new InMemorySecretVault();
        var relayHandler = new QueueHandler();
        relayHandler.Enqueue("""{"message_id":"reply-repaired","platform_message_id":"platform-repaired"}""");
        var callbackScheduler = new NoopCallbackScheduler();
        using var actorNetwork = new ContractActorNetwork(
            CreateOutboundPort(relayHandler),
            new SecretVaultWorkflowResultDeliveryCredentialResolver(secretVault),
            callbackScheduler);

        var documentStore = new RegistrationDocumentStore();
        var projector = new ChannelBotRegistrationProjector(documentStore, new SystemProjectionClock());
        var projectionHook = new RegistrationProjectionHook(projector);
        var registrationLogger = new RecordingLogger<ChannelBotRegistrationGAgent>();
        using var registrationServices = BuildEventSourcingServices(callbackScheduler, projectionHook);
        var registrationAgent = new ChannelBotRegistrationGAgent
        {
            Services = registrationServices,
            EventSourcingBehaviorFactory =
                registrationServices.GetRequiredService<IEventSourcingBehaviorFactory<ChannelBotRegistrationStoreState>>(),
            Logger = registrationLogger,
        };
        SetId(registrationAgent, ChannelBotRegistrationGAgent.WellKnownId);
        await registrationAgent.ActivateAsync();
        actorNetwork.RegisterActivated(ChannelBotRegistrationGAgent.WellKnownId, registrationAgent);

        await registrationAgent.HandleRegister(new ChannelBotRegisterCommand
        {
            RequestedId = registrationId,
            Platform = "lark",
            ScopeId = "scope-alpha",
            NyxProviderSlug = "api-lark-bot-alpha",
            WebhookUrl = "https://nyx.example/api/v1/webhooks/channel/lark/bot-alpha",
            NyxChannelBotId = "bot-alpha",
            NyxAgentApiKeyId = "key-old-alpha",
            NyxConversationRouteId = "route-alpha",
            DefaultSkillName = "team-entry-alpha",
        });
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            "scope-alpha",
            "key-new-alpha",
            rawRepairedAgentKey,
            "channel workflow delivery repair contract"));

        await registrationAgent.HandleWorkflowResultDeliveryRepairRequest(new()
        {
            RegistrationId = registrationId,
            RequestId = requestId,
            ExpectedApiKeyId = "key-old-alpha",
            ExpectedConversationRouteId = "route-alpha",
            RequestedBySubjectId = "user-alpha",
            RequestedAtUnixMs = 1784563200000,
        });
        await registrationAgent.HandleWorkflowResultDeliveryRepairPrepare(new()
        {
            RegistrationId = registrationId,
            RequestId = requestId,
            ExpectedApiKeyId = "key-old-alpha",
            RotatedApiKeyId = "key-new-alpha",
            PreparedSecretReference = stored.Reference.Clone(),
            UpdatedAtUnixMs = 1784563201000,
        });

        var registrationQuery = new ChannelBotRegistrationQueryPort(documentStore);
        var preparingRegistration = await registrationQuery.GetAsync(registrationId);
        preparingRegistration.Should().NotBeNull();
        ChannelWorkflowResultDeliveryCapability.Resolve(preparingRegistration!)
            .Should().Be(ChannelWorkflowResultDeliveryCapabilityStatus.Repairing);
        preparingRegistration.NyxAgentApiKeyId.Should().Be("key-old-alpha");

        await registrationAgent.HandleWorkflowResultDeliveryRepairComplete(new()
        {
            RegistrationId = registrationId,
            RequestId = requestId,
            ExpectedApiKeyId = "key-old-alpha",
            RotatedApiKeyId = "key-new-alpha",
            PreparedSecretReference = stored.Reference.Clone(),
            UpdatedAtUnixMs = 1784563202000,
        });

        var projectedRegistration = await registrationQuery.GetAsync(registrationId);
        projectedRegistration.Should().NotBeNull();
        projectedRegistration!.NyxChannelBotId.Should().Be("bot-alpha");
        projectedRegistration.NyxConversationRouteId.Should().Be("route-alpha");
        projectedRegistration.WebhookUrl.Should().Be(
            "https://nyx.example/api/v1/webhooks/channel/lark/bot-alpha");
        projectedRegistration.ScopeId.Should().Be("scope-alpha");
        projectedRegistration.DefaultSkillName.Should().Be("team-entry-alpha");
        projectedRegistration.NyxAgentApiKeyId.Should().Be("key-new-alpha");
        projectedRegistration.WorkflowResultDeliveryCredential.Should().Be(stored.Reference);
        projectedRegistration.WorkflowResultDeliveryRepair.Should().BeNull();
        ChannelWorkflowResultDeliveryCapability.Resolve(projectedRegistration)
            .Should().Be(ChannelWorkflowResultDeliveryCapabilityStatus.Enabled);

        var safeRepairResult = new ChannelWorkflowResultDeliveryRepairResult(
            ChannelWorkflowResultDeliveryRepairResultStatus.Repaired,
            requestId,
            registrationId,
            "key-new-alpha");
        var httpShapedRepairJson = JsonSerializer.Serialize(
            safeRepairResult,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var turnRunner = CreateTurnRunner(registrationQuery);
        var turn = await turnRunner.RunInboundAsync(
            BuildInboundActivity(registrationId),
            CancellationToken.None);
        turn.Success.Should().BeTrue();
        turn.LlmReplyRequest.Should().NotBeNull();
        var toolContext = AgentToolExecutionContextMapper.FromPayload(turn.LlmReplyRequest!.ToolContext);
        toolContext.Caller.ScopeId.Should().Be("scope-alpha");
        toolContext.Channel.BotRegistrationId.Should().Be(registrationId);
        toolContext.Channel.WorkflowResultDeliveryCredential.Should().NotBeNull();
        toolContext.Channel.WorkflowResultDeliveryCredential!.SecretReference.Should().Be(stored.Reference);
        toolContext.Channel.WorkflowResultDeliveryCredential.SubjectId.Should().Be("key-new-alpha");

        var workflowDispatch = new ContractWorkflowDispatchService(actorNetwork);
        var teamResolver = new ContractTeamEntryMemberResolver();
        var serviceResolution = new ContractServiceInvocationResolutionPort();
        var serviceDispatcher = new ContractWorkflowServiceInvocationDispatcher(workflowDispatch);
        var admissionAuthorizer = new ContractInvokeAdmissionAuthorizer();
        var deliveryRegistration = new WorkflowRunBackgroundDeliveryRegistrationPort(
            actorNetwork,
            actorNetwork,
            NullLogger<WorkflowRunBackgroundDeliveryRegistrationPort>.Instance);
        var invocationLogger = new RecordingLogger<AevatarInvocationDispatcher>();
        var invocationDispatcher = CreateInvocationDispatcher(
            actorNetwork,
            workflowDispatch,
            deliveryRegistration,
            teamResolver,
            serviceResolution,
            serviceDispatcher,
            admissionAuthorizer,
            invocationLogger);

        using var toolContextScope = AgentToolContextScope.Push(toolContext);
        var result = await invocationDispatcher.InvokeTeamForChatRunAsync(
            null,
            """
            {
              "team_id": "team-alpha",
              "endpoint_id": "chat",
              "payload": { "prompt": "invoice repaired" },
              "wait": "stream"
            }
            """);

        result.ErrorCode.Should().BeEmpty();
        result.Status.Should().Be("streaming");
        result.ServiceId.Should().Be("svc-alpha");
        teamResolver.LastScopeId.Should().Be("scope-alpha");
        teamResolver.LastTeamId.Should().Be("team-alpha");
        serviceResolution.Request.Should().NotBeNull();
        serviceResolution.Request!.Identity.ServiceId.Should().Be("svc-alpha");
        admissionAuthorizer.Calls.Should().Be(1);
        serviceDispatcher.Request.Should().NotBeNull();

        var deliveryAgent = actorNetwork.Agents
            .OfType<WorkflowRunDeliveryGAgent>()
            .Should()
            .ContainSingle()
            .Subject;
        deliveryAgent.State.Status.Should().Be(WorkflowRunDeliveryStatus.Delivered);
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Authorization.Should().Be($"Bearer {rawRepairedAgentKey}");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"invoice repaired\"");

        registrationAgent.State.ToString().Should().NotContain(rawRepairedAgentKey);
        documentStore.Documents[registrationId].ToString().Should().NotContain(rawRepairedAgentKey);
        projectionHook.Publications.Should().OnlyContain(publication =>
            !Encoding.UTF8.GetString(publication.ToByteArray()).Contains(
                rawRepairedAgentKey,
                StringComparison.Ordinal));
        safeRepairResult.ToString().Should().NotContain(rawRepairedAgentKey)
            .And.NotContain(stored.Reference.Ref);
        httpShapedRepairJson.Should().NotContain(rawRepairedAgentKey)
            .And.NotContain(stored.Reference.Ref);
        result.ToolExecutionResultJson.Should().NotContain(rawRepairedAgentKey)
            .And.NotContain(stored.Reference.Ref);
        registrationLogger.Messages.Concat(invocationLogger.Messages).Should().OnlyContain(message =>
            !message.Contains(rawRepairedAgentKey, StringComparison.Ordinal) &&
            !message.Contains(stored.Reference.Ref, StringComparison.Ordinal));
    }

    private static ServiceProvider BuildEventSourcingServices(
        IActorRuntimeCallbackScheduler callbackScheduler,
        ICommittedStatePublicationHook? publicationHook = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton(callbackScheduler)
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        if (publicationHook is not null)
            services.AddSingleton(publicationHook);
        return services.BuildServiceProvider();
    }

    private static ChannelConversationTurnRunner CreateTurnRunner(ChannelBotRegistrationQueryPort registrationQuery)
    {
        var nyxHandler = new QueueHandler();
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(nyxHandler) { BaseAddress = new Uri("https://nyx.example.com") },
            NullLogger<NyxIdApiClient>.Instance);
        var adapter = Substitute.For<IPlatformAdapter>();
        adapter.Platform.Returns("lark");
        return new ChannelConversationTurnRunner(
            new ServiceCollection().BuildServiceProvider(),
            registrationQuery,
            registrationQuery,
            [adapter],
            nyxClient,
            CreateOutboundPort(nyxHandler),
            null,
            NullLogger<ChannelConversationTurnRunner>.Instance,
            toolExecutionPort: Substitute.For<IAgentToolExecutionPort>());
    }

    private static AevatarInvocationDispatcher CreateInvocationDispatcher(
        IActorDispatchPort actorDispatchPort,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
            workflowDispatchService,
        IWorkflowRunBackgroundDeliveryRegistrationPort deliveryRegistration,
        ITeamEntryMemberResolver? teamEntryMemberResolver = null,
        IServiceInvocationResolutionPort? serviceInvocationResolutionPort = null,
        IServiceInvocationDispatcher? serviceInvocationDispatcher = null,
        IInvokeAdmissionAuthorizer? admissionAuthorizer = null,
        ILogger<AevatarInvocationDispatcher>? logger = null) =>
        new(
            actorDispatchPort,
            Substitute.For<IGAgentActorRegistryQueryPort>(),
            teamEntryMemberResolver ?? Substitute.For<ITeamEntryMemberResolver>(),
            Substitute.For<IMemberPublishedServiceResolver>(),
            Substitute.For<IStaticGAgentStreamInvocationPort<AGUIEvent>>(),
            workflowDispatchService,
            serviceInvocationResolutionPort ?? Substitute.For<IServiceInvocationResolutionPort>(),
            serviceInvocationDispatcher ?? Substitute.For<IServiceInvocationDispatcher>(),
            admissionAuthorizer ?? Substitute.For<IInvokeAdmissionAuthorizer>(),
            Substitute.For<IServiceRunQueryPort>(),
            Substitute.For<IGAgentRunTerminalQueryPort>(),
            Substitute.For<IWorkflowExecutionQueryApplicationService>(),
            deliveryRegistration,
            logger ?? NullLogger<AevatarInvocationDispatcher>.Instance);

    private static ChatActivity BuildInboundActivity(string registrationId) =>
        new()
        {
            Id = "reply-message-1",
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From(registrationId),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From(registrationId),
                ConversationScope.DirectMessage,
                "conversation-1",
                "dm",
                "sender-1"),
            From = new ParticipantRef
            {
                CanonicalId = "sender-1",
                DisplayName = "Approver",
            },
            Content = new MessageContent { Text = "Please run the approval workflow." },
            TransportExtras = new TransportExtras
            {
                NyxPlatform = "lark",
                NyxPlatformMessageId = "platform-message-1",
            },
        };

    private static NyxIdRelayOutboundPort CreateOutboundPort(HttpMessageHandler handler) =>
        new(
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") },
                NullLogger<NyxIdApiClient>.Instance),
            NullLogger<NyxIdRelayOutboundPort>.Instance,
            [new PlainTextComposer("lark")]);

    private static void SetId(GAgentBase agent, string actorId) =>
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);

    private sealed class RegistrationProjectionHook(ChannelBotRegistrationProjector projector)
        : ICommittedStatePublicationHook
    {
        public List<CommittedStateEventPublished> Publications { get; } = [];

        public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            var published = context.Published.Clone();
            Publications.Add(published);
            await projector.ProjectAsync(
                new ChannelBotRegistrationMaterializationContext
                {
                    RootActorId = context.ActorId,
                    ProjectionKind = "channel-bot-registration-current-state",
                },
                new EventEnvelope
                {
                    Id = published.StateEvent.EventId,
                    Timestamp = published.StateEvent.Timestamp?.Clone()
                                ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Payload = Any.Pack(published),
                    Route = EnvelopeRouteSemantics.CreateObserverPublication(context.ActorId),
                },
                ct);
        }
    }

    private sealed class RegistrationDocumentStore
        : IProjectionWriteDispatcher<ChannelBotRegistrationDocument>,
          IProjectionDocumentReader<ChannelBotRegistrationDocument, string>
    {
        public Dictionary<string, ChannelBotRegistrationDocument> Documents { get; } =
            new(StringComparer.Ordinal);

        public Task<ProjectionWriteResult> UpsertAsync(
            ChannelBotRegistrationDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Documents[readModel.Id] = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Documents.Remove(id);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ChannelBotRegistrationDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Documents.TryGetValue(key, out var document)
                ? document.Clone()
                : null);
        }

        public Task<ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>
            {
                Items = Documents.Values.Take(query.Take).Select(static document => document.Clone()).ToArray(),
            });
        }
    }

    private sealed class SystemProjectionClock : IProjectionClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class ContractWorkflowDispatchService(ContractActorNetwork actorNetwork)
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public WorkflowChatRunRequest? Command { get; private set; }
        public WorkflowRunGAgent? Agent { get; private set; }

        public async Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Command = command;
            var source = command.Source.InlineBundle
                         ?? throw new InvalidOperationException("The contract workflow must use an inline YAML bundle.");
            var workflowYaml = source.YamlDocuments.FirstOrDefault()?.Yaml
                               ?? throw new InvalidOperationException("The contract workflow YAML is missing.");
            var actorId = string.IsNullOrWhiteSpace(source.ActorId) ? WorkflowActorId : source.ActorId.Trim();
            var workflowName = string.IsNullOrWhiteSpace(source.EntryName) ? "wf-main" : source.EntryName.Trim();

            Agent = await actorNetwork.CreateWorkflowRunAsync(actorId, ct);
            await Agent.BindWorkflowRunDefinitionAsync(
                definitionActorId: "contract-inline-definition",
                workflowYaml: workflowYaml,
                workflowName: workflowName,
                inlineWorkflowYamls: null,
                runId: actorId,
                scopeId: command.ScopeId,
                runOrigin: null,
                scheduleId: null,
                capabilityAdmissionPlan: null,
                expectedExecutionMode: command.ExpectedExecutionMode,
                ct: ct);

            var context = new DefaultCommandContextPolicy().Create(
                actorId,
                command.Headers,
                command.CommandIdSeed,
                command.CorrelationIdSeed);
            var envelope = WorkflowChatRequestContractMapper.CreateEnvelope(command, context);
            var admission = await actorNetwork.DispatchAsync(actorId, envelope, ct);
            return CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt(
                    actorId,
                    workflowName,
                    context.CommandId,
                    context.CorrelationId),
                admission);
        }
    }

    private sealed class ContractTeamEntryMemberResolver : ITeamEntryMemberResolver
    {
        public string? LastScopeId { get; private set; }
        public string? LastTeamId { get; private set; }

        public Task<TeamEntryMemberResolution> ResolveAsync(
            string scopeId,
            string teamId,
            string endpointId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastScopeId = scopeId;
            LastTeamId = teamId;
            return Task.FromResult(new TeamEntryMemberResolution(
                "scope-alpha",
                "team-alpha",
                "m-alpha",
                "svc-alpha"));
        }
    }

    private sealed class ContractServiceInvocationResolutionPort : IServiceInvocationResolutionPort
    {
        public ServiceInvocationRequest? Request { get; private set; }

        public Task<bool> HasServiceAsync(
            ServiceIdentity identity,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(string.Equals(identity.ServiceId, "svc-alpha", StringComparison.Ordinal));
        }

        public Task<ServiceInvocationResolvedTarget> ResolveAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Request = request.Clone();
            var endpoint = new ServiceEndpointDescriptor
            {
                EndpointId = "chat",
                DisplayName = "Workflow chat",
                Kind = ServiceEndpointKind.Chat,
                RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
            };
            var artifact = new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-alpha",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "svc-alpha",
                },
                RevisionId = "rev-alpha",
                ImplementationKind = ServiceImplementationKind.Workflow,
                ArtifactHash = "artifact-alpha",
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "wf-alpha",
                        WorkflowYaml = "name: wf-alpha\nroles: []\nsteps:\n  - id: result\n    type: transform",
                        DefinitionActorId = "wf-definition-alpha",
                        ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
                    },
                },
            };
            artifact.Endpoints.Add(endpoint.Clone());
            return Task.FromResult(new ServiceInvocationResolvedTarget(
                new ServiceInvocationResolvedService(
                    "scope-alpha:default:default:svc-alpha",
                    "rev-alpha",
                    "deployment-alpha",
                    "wf-definition-alpha",
                    "Active",
                    []),
                artifact,
                endpoint));
        }
    }

    private sealed class ContractWorkflowServiceInvocationDispatcher(
        ContractWorkflowDispatchService workflowDispatch)
        : IServiceInvocationDispatcher
    {
        public ServiceInvocationRequest? Request { get; private set; }

        public async Task<ServiceInvocationAcceptedReceipt> DispatchAsync(
            ServiceInvocationResolvedTarget target,
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            Request = request.Clone();
            var chatRequest = request.Payload?.Unpack<ChatRequestEvent>()
                              ?? throw new InvalidOperationException(
                                  "The contract workflow Team entry requires a ChatRequestEvent payload.");
            var plan = target.Artifact.DeploymentPlan?.WorkflowPlan
                       ?? throw new InvalidOperationException(
                           "The contract workflow Team entry requires a workflow deployment plan.");
            var notification = request.WorkflowCompletionNotificationTarget is null
                ? null
                : new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCompletionNotificationTarget(
                    request.WorkflowCompletionNotificationTarget.ActorId,
                    request.WorkflowCompletionNotificationTarget.DeliveryId,
                    request.WorkflowCompletionNotificationTarget.ExpiresAtUnixMs);
            var dispatched = await workflowDispatch.DispatchAsync(
                new WorkflowChatRunRequest(
                    Prompt: chatRequest.Prompt,
                    Source: WorkflowChatSource.InlineYamlBundle(
                        [plan.WorkflowYaml],
                        plan.WorkflowName,
                        WorkflowActorId),
                    ExpectedExecutionMode: plan.ExecutionMode,
                    SessionId: chatRequest.SessionId,
                    ScopeId: request.Identity?.TenantId ?? chatRequest.ScopeId,
                    CommandIdSeed: request.CommandId,
                    CorrelationIdSeed: request.CorrelationId,
                    CompletionNotificationTarget: notification),
                ct);
            if (!dispatched.Succeeded || dispatched.Receipt is null)
                throw new InvalidOperationException("The contract workflow Team entry was not accepted.");

            var receipt = dispatched.Receipt;
            return new ServiceInvocationAcceptedReceipt
            {
                RequestId = receipt.CommandId,
                ServiceKey = target.Service.ServiceKey,
                DeploymentId = target.Service.DeploymentId,
                TargetActorId = receipt.ActorId,
                EndpointId = target.Endpoint.EndpointId,
                CommandId = receipt.CommandId,
                CorrelationId = receipt.CorrelationId,
                RunId = receipt.ActorId,
            };
        }
    }

    private sealed class ContractInvokeAdmissionAuthorizer : IInvokeAdmissionAuthorizer
    {
        public int Calls { get; private set; }

        public Task AuthorizeAsync(
            string serviceKey,
            string deploymentId,
            PreparedServiceRevisionArtifact artifact,
            ServiceEndpointDescriptor endpoint,
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static class WorkflowChatRequestContractMapper
    {
        public static EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
        {
            var sessionId = string.IsNullOrWhiteSpace(command.SessionId)
                ? context.CorrelationId
                : command.SessionId;
            var payload = new WorkflowChatRequestEvent
            {
                Prompt = command.Prompt ?? string.Empty,
                SessionId = sessionId,
                ScopeId = command.ScopeId ?? string.Empty,
                CallerCredential = new Aevatar.Workflow.Abstractions.WorkflowCallerCredential
                {
                    BearerToken = command.CallerCredential?.BearerToken ?? string.Empty,
                },
            };
            payload.Headers[WorkflowRunCommandMetadataKeys.SessionId] = sessionId;
            if (command.CompletionNotificationTarget is not null)
            {
                payload.CompletionNotificationTarget = new Aevatar.Workflow.Abstractions.WorkflowCompletionNotificationTarget
                {
                    ActorId = command.CompletionNotificationTarget.ActorId,
                    DeliveryId = command.CompletionNotificationTarget.DeliveryId,
                    ExpiresAtUnixMs = command.CompletionNotificationTarget.ExpiresAtUnixMs,
                };
            }

            return new EventEnvelope
            {
                Id = context.CommandId,
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(payload),
                Route = EnvelopeRouteSemantics.CreateDirect("api", context.TargetId),
                Propagation = new EnvelopePropagation { CorrelationId = context.CorrelationId },
            };
        }
    }

    private sealed class ContractActorNetwork : IActorRuntime, IActorDispatchPort, IDisposable
    {
        private readonly Dictionary<string, ContractActor> _actors = new(StringComparer.Ordinal);
        private readonly Queue<(ContractActor Actor, EventEnvelope Envelope, CancellationToken CancellationToken)>
            _inbox = new();
        private readonly ServiceProvider _services;
        private readonly NyxIdRelayOutboundPort _outboundPort;
        private readonly IWorkflowResultDeliveryCredentialResolver _credentialResolver;
        private readonly IActorRuntimeCallbackScheduler _callbackScheduler;
        private bool _draining;

        public ContractActorNetwork(
            NyxIdRelayOutboundPort outboundPort,
            IWorkflowResultDeliveryCredentialResolver credentialResolver,
            IActorRuntimeCallbackScheduler callbackScheduler)
        {
            _outboundPort = outboundPort;
            _credentialResolver = credentialResolver;
            _callbackScheduler = callbackScheduler;
            _services = BuildEventSourcingServices(callbackScheduler);
        }

        public IReadOnlyList<IAgent> Agents => _actors.Values.Select(static actor => actor.Agent).ToArray();
        public List<(string ActorId, string EventType)> Dispatches { get; } = [];

        public void RegisterActivated(string actorId, IAgent agent) =>
            _actors.Add(actorId, new ContractActor(actorId, agent));

        public int IndexOf<TEvent>() where TEvent : IMessage =>
            Dispatches.FindIndex(dispatch =>
                string.Equals(dispatch.EventType, Any.Pack(Activator.CreateInstance<TEvent>()).TypeUrl, StringComparison.Ordinal));

        public async Task<WorkflowRunGAgent> CreateWorkflowRunAsync(
            string actorId,
            CancellationToken ct = default)
        {
            if (_actors.TryGetValue(actorId, out var existing))
                return (WorkflowRunGAgent)existing.Agent;

            var agent = new WorkflowRunGAgent(
                this,
                this,
                new ContractWorkflowModuleFactory(),
                [new WorkflowCoreModulePack()])
            {
                Services = _services,
                EventSourcingBehaviorFactory =
                    _services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunState>>(),
                EventPublisher = new ActorNetworkPublisher(actorId, this),
                Logger = NullLogger.Instance,
            };
            SetId(agent, actorId);
            var actor = new ContractActor(actorId, agent);
            _actors.Add(actorId, actor);
            await actor.ActivateAsync(ct);
            return agent;
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public async Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            if (agentType != typeof(WorkflowRunDeliveryGAgent))
                throw new NotSupportedException($"Contract actor creation does not support {agentType.Name}.");
            var actorId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
            if (_actors.TryGetValue(actorId, out var existing))
                return existing;

            var agent = new WorkflowRunDeliveryGAgent(
                _outboundPort,
                _credentialResolver,
                _callbackScheduler,
                NullLogger<WorkflowRunDeliveryGAgent>.Instance)
            {
                Services = _services,
                EventSourcingBehaviorFactory =
                    _services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunDeliveryGAgentState>>(),
                EventPublisher = new ActorNetworkPublisher(actorId, this),
            };
            SetId(agent, actorId);
            var actor = new ContractActor(actorId, agent);
            _actors.Add(actorId, actor);
            await actor.ActivateAsync(ct);
            return actor;
        }

        public async Task DestroyAsync(string id, CancellationToken ct = default)
        {
            if (_actors.Remove(id, out var actor))
                await actor.DeactivateAsync(ct);
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_actors.GetValueOrDefault(id));

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            if (!_actors.TryGetValue(actorId, out var actor))
                throw new InvalidOperationException($"Actor '{actorId}' is not registered.");
            Dispatches.Add((actorId, envelope.Payload?.TypeUrl ?? string.Empty));
            _inbox.Enqueue((actor, envelope.Clone(), ct));
            var admission = DispatchAdmissionFactory.Create(actorId, envelope);
            if (_draining)
                return admission;

            _draining = true;
            try
            {
                while (_inbox.TryDequeue(out var pending))
                    await pending.Actor.HandleEventAsync(pending.Envelope, pending.CancellationToken);
            }
            finally
            {
                _draining = false;
            }

            return admission;
        }

        public void Dispose()
        {
            _actors.Clear();
            _services.Dispose();
        }
    }

    private sealed class ActorNetworkPublisher(string publisherActorId, ContractActorNetwork actorNetwork)
        : IEventPublisher
    {
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where TEvent : IMessage =>
            audience == TopologyAudience.Self
                ? actorNetwork.DispatchAsync(
                    publisherActorId,
                    CreateEnvelope(publisherActorId, publisherActorId, evt, sourceEnvelope, self: true),
                    ct)
                : Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where TEvent : IMessage =>
            actorNetwork.DispatchAsync(
                targetActorId,
                CreateEnvelope(publisherActorId, targetActorId, evt, sourceEnvelope, self: false),
                ct);

        private static EventEnvelope CreateEnvelope<TEvent>(
            string publisherActorId,
            string targetActorId,
            TEvent evt,
            EventEnvelope? sourceEnvelope,
            bool self)
            where TEvent : IMessage =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(evt),
                Route = self
                    ? EnvelopeRouteSemantics.CreateTopologyPublication(publisherActorId, TopologyAudience.Self)
                    : EnvelopeRouteSemantics.CreateDirect(publisherActorId, targetActorId),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = sourceEnvelope?.Propagation?.CorrelationId ?? string.Empty,
                },
            };
    }

    private sealed class ContractActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;
        public Task ActivateAsync(CancellationToken ct = default) => Agent.ActivateAsync(ct);
        public Task DeactivateAsync(CancellationToken ct = default) => Agent.DeactivateAsync(ct);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Agent.HandleEventAsync(envelope, ct);
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class ContractWorkflowModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            module = string.Equals(name, "transform", StringComparison.Ordinal)
                ? new TransformModule()
                : null;
            return module is not null;
        }
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new();

        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        public void Enqueue(string body) => _responses.Enqueue(body);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more queued responses.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class PlainTextComposer(string platform) : IMessageComposer<PlainTextPayload>
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);
        public PlainTextPayload Compose(MessageContent intent, ComposeContext context) =>
            new(intent.Text ?? string.Empty);
        object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);
        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) => ComposeCapability.Exact;
    }

    private sealed record PlainTextPayload(string PlainText) : IPlainTextComposedMessage;
}
