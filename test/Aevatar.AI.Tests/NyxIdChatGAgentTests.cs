using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Diagnostics.Metrics;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Observability;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using AGUIEvent = Aevatar.AGUI.Contracts.AGUIEvent;

namespace Aevatar.AI.Tests;

public class NyxIdChatGAgentTests
{
    private const string ExactSkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string ExactSkillVersion = "1.2";
    private const string ExactSkillName = "skill-alpha";
    private const string ExactSkillPublisher = "publisher-alpha";
    private static readonly ByteString ExactSkillSha256 =
        ByteString.CopyFrom(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

    [Fact]
    public void ConversationCreateCommand_ShouldExposeExplicitAgentProfileReference()
    {
        NyxIdChatConversationCreateCommand.Descriptor.Fields
            .InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Equal(
                "scope_id",
                "created_locally",
                "agent_profile",
                "first_turn",
                "requested_actor_id",
                "agent_profile_reference");
    }

    [Fact]
    public void AgentProfileSelection_ShouldUseAnAsyncResolverContract()
    {
        var resolver = typeof(NyxIdChatGAgent).Assembly.GetType(
            "Aevatar.GAgents.NyxidChat.AgentProfiles.INyxIdChatAgentProfileResolver");

        resolver.Should().NotBeNull();
        resolver!.GetMethod("ResolveAsync")!.ReturnType.IsGenericType.Should().BeTrue();
        resolver.GetMethod("ResolveAsync")!.ReturnType.GetGenericTypeDefinition()
            .Should().Be(typeof(Task<>));
    }

    [Fact]
    public async Task CreateTargetResolver_ShouldCopySelectedProfileForMatchingDirectRoute()
    {
        var runtime = new RecordingActorRuntime();
        var source = new FixedAgentProfileResolver(BuildSealedProfile("profile-v1", "profile.route"));
        var routeQueryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel
                {
                    ToolSetRef = new ChatRouteToolSetRef { Name = "profile.route" },
                },
            },
            []));
        var resolver = new NyxIdChatConversationCreateCommandTargetResolver(
            runtime,
            routeQueryPort,
            NewChatRouteResolver(),
            source);
        var command = new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            AgentProfileReference = new AgentProfileReference
            {
                OwnerKind = AgentProfileReferenceOwnerKind.Caller,
                ProfileSlug = "research-assistant",
            },
        };

        var result = await resolver.ResolveAsync(command);

        result.Succeeded.Should().BeTrue();
        source.ResolveCalls.Should().Be(1);
        runtime.CreateCalls.Should().ContainSingle().Which.Type.Should()
            .Be(typeof(NyxIdChatConversationGAgent));
        source.Requests.Select(static request => request.ScopeId).Should().Equal("scope-a");
        source.Requests.Should().ContainSingle().Which.ExplicitReference.Should()
            .BeEquivalentTo(command.AgentProfileReference);
        source.Requests[0].ExplicitReference.Should().NotBeSameAs(command.AgentProfileReference);
        command.AgentProfile.Should().NotBeNull();
        AgentProfileSnapshotCodec.ByteEquivalent(command.AgentProfile, source.Snapshot).Should().BeTrue();
        command.AgentProfile.Should().NotBeSameAs(source.Snapshot);
    }

    [Fact]
    public async Task CreateTargetResolver_ShouldPreserveFirstDispatchOwnershipWhenRequestedActorAlreadyExists()
    {
        var runtime = new RecordingActorRuntime();
        var source = new FixedAgentProfileResolver(BuildSealedProfile("profile-v1", "profile.route"));
        var routeQueryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel
                {
                    ToolSetRef = new ChatRouteToolSetRef { Name = "profile.route" },
                },
            },
            []));
        var resolver = new NyxIdChatConversationCreateCommandTargetResolver(
            runtime,
            routeQueryPort,
            NewChatRouteResolver(),
            source);
        var command = new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            RequestedActorId = "nyxid-chat-retry",
        };

        var result = await resolver.ResolveAsync(command);

        result.Succeeded.Should().BeTrue();
        result.Target!.CreatedLocally.Should().BeTrue();
        source.Requests.Select(static request => request.ScopeId).Should().Equal("scope-a");
        AgentProfileSnapshotCodec.ByteEquivalent(command.AgentProfile, source.Snapshot).Should().BeTrue();
    }

    [Fact]
    public async Task CreateTargetResolver_ShouldRejectProfileRouteDriftBeforeCreatingActor()
    {
        var runtime = new RecordingActorRuntime();
        var source = new FixedAgentProfileResolver(BuildSealedProfile("profile-v1", "reviewed.route"));
        var routeQueryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel
                {
                    ToolSetRef = new ChatRouteToolSetRef { Name = "drifted.route" },
                },
            },
            []));
        var resolver = new NyxIdChatConversationCreateCommandTargetResolver(
            runtime,
            routeQueryPort,
            NewChatRouteResolver(),
            source);

        var result = await resolver.ResolveAsync(new NyxIdChatConversationCreateCommand { ScopeId = "scope-a" });

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(NyxIdChatLifecycleCommandStartError.AdmissionUnavailable);
        source.ResolveCalls.Should().Be(1);
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateTargetResolver_ShouldRejectProfileRouteWithoutCompleteToolSetRef(
        bool missingForwardToModel)
    {
        var runtime = new RecordingActorRuntime();
        var source = new FixedAgentProfileResolver(BuildSealedProfile("profile-v1", "reviewed.route"));
        var routeSnapshot = missingForwardToModel
            ? null
            : new ChatRoutePolicySnapshot(
                new ChatRouteAction { ForwardToModel = new ForwardToModel() },
                []);
        var routeQueryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(routeSnapshot);
        var routeResolver = missingForwardToModel
            ? new ChatRouteResolver(new MissingForwardToModelFallbackProvider())
            : NewChatRouteResolver();
        var resolver = new NyxIdChatConversationCreateCommandTargetResolver(
            runtime,
            routeQueryPort,
            routeResolver,
            source);
        var command = new NyxIdChatConversationCreateCommand { ScopeId = "scope-a" };

        var result = await resolver.ResolveAsync(command);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(NyxIdChatLifecycleCommandStartError.AdmissionUnavailable);
        source.ResolveCalls.Should().Be(1);
        runtime.CreateCalls.Should().BeEmpty();
        command.AgentProfile.Should().BeNull();
    }

    [Fact]
    public async Task CreateTargetResolver_ShouldFailClosedWhenProfileResolutionIsUnavailable()
    {
        var runtime = new RecordingActorRuntime();
        var source = FixedAgentProfileResolver.Failure(
            NyxIdChatAgentProfileResolutionStatus.ReadModelUnavailable);
        var routeQueryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel
                {
                    ToolSetRef = new ChatRouteToolSetRef { Name = "profile.route" },
                },
            },
            []));
        var resolver = new NyxIdChatConversationCreateCommandTargetResolver(
            runtime,
            routeQueryPort,
            NewChatRouteResolver(),
            source);

        var result = await resolver.ResolveAsync(new NyxIdChatConversationCreateCommand { ScopeId = "scope-a" });

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(NyxIdChatLifecycleCommandStartError.AdmissionUnavailable);
        source.ResolveCalls.Should().Be(1);
        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldBindProfileBeforeCreationAndRegistrationEvents()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        using var provider = BuildServiceProvider(registry, new RecordingActorRuntime());
        const string actorId = "nyxid-chat-profile-order";
        var agent = CreateConversationAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
            AgentProfile = BuildSealedProfile("profile-v1"),
        }));

        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        events.Select(static stateEvent => stateEvent.EventData.TypeUrl).Should().Equal(
            Any.Pack(new AgentProfileBoundEvent()).TypeUrl,
            Any.Pack(new NyxIdChatConversationCreationStartedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatConversationRegistrationAcceptedEvent()).TypeUrl);
        agent.State.AgentProfile.ProfileVersion.Should().Be("profile-v1");
        registry.RegisteredActors.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldAtomicallyPrepareHistoryInitializationAfterVisibleRegistration()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        var history = new RecordingChatHistoryCommandPort();
        var dispatch = new RecordingSelfDispatchPort();
        using var provider = BuildServiceProvider(
            registry,
            new RecordingActorRuntime(),
            history);
        const string actorId = "nyxid-chat-history-initialize";
        var agent = CreateConversationAgent(provider, actorId, dispatch);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
        }));

        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        var accepted = events.Should().ContainSingle(stateEvent =>
                stateEvent.EventData.Is(NyxIdChatConversationRegistrationAcceptedEvent.Descriptor))
            .Which.EventData.Unpack<NyxIdChatConversationRegistrationAcceptedEvent>();
        accepted.State.Should().NotBeNull();
        var outbox = accepted.State.PendingHistoryInitialization;
        outbox.Should().NotBeNull();
        outbox.OperationId.Should().NotBeNullOrWhiteSpace();
        outbox.ScopeId.Should().Be("scope-a");
        outbox.ConversationId.Should().Be(actorId);
        outbox.ServiceId.Should().Be(actorId);
        outbox.ServiceKind.Should().Be(NyxIdChatServiceDefaults.GAgentKind);
        outbox.Attempt.Should().Be(1);
        accepted.State.HistoryInitializationOperationId.Should().Be(outbox.OperationId);
        agent.State.ToByteString().Should().Equal(accepted.State.ToByteString());

        var signalEnvelope = dispatch.Calls.Should().ContainSingle().Which.Envelope;
        signalEnvelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        var signal = signalEnvelope.Payload
            .Unpack<NyxIdChatHistoryInitializationDispatchRequested>();
        signal.OperationId.Should().Be(outbox.OperationId);
        signal.Attempt.Should().Be(1);
        history.Initializations.Should().BeEmpty(
            "the post-commit continuation must re-enter the actor inbox");
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WithFirstTurn_ShouldRegisterBeforeStartingTurn()
    {
        var operations = new List<string>();
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var history = new RecordingChatHistoryCommandPort(operations);
        var runtime = new RecordingActorRuntime(operations);
        var dispatch = new RecordingSelfDispatchPort(operations);
        using var provider = BuildServiceProvider(registry, runtime, history);
        const string actorId = "nyxid-chat-first-turn";
        var agent = CreateConversationAgent(provider, actorId, dispatch);
        var firstTurn = new NyxIdChatStartTurnCommand
        {
            ScopeId = "scope-a",
            ConversationActorId = actorId,
            TurnId = "turn-first",
            TaskId = "task-first",
            ClientRequestId = "client-first",
            CommandId = "command-first",
            CorrelationId = "correlation-first",
            Prompt = "hello",
            ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
            {
                Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
                {
                    OwnerSubject = "owner-alpha",
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
            FirstTurn = firstTurn,
        }));

        operations.IndexOf("registry.register").Should().BeLessThan(
            operations.IndexOf("history.reserve"));
        agent.State.ActiveTurn.TurnId.Should().Be("turn-first");
        dispatch.Calls.Should().Contain(call =>
            call.Envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor));
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenInitializationContinuationDispatchFails_ShouldKeepAcceptedConversationPendingRecovery()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingSelfDispatchPort
        {
            DispatchException = new InvalidOperationException("self dispatch unavailable"),
        };
        using var provider = BuildServiceProvider(registry, runtime);
        const string actorId = "nyxid-chat-history-post-accept-dispatch-failure";
        var agent = CreateConversationAgent(provider, actorId, dispatch);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
        }));

        agent.State.PendingHistoryInitialization.Should().NotBeNull();
        registry.RegisteredActors.Should().ContainSingle();
        registry.UnregisteredActors.Should().BeEmpty(
            "post-accept continuation delivery must recover from the durable outbox");
        runtime.DestroyedActors.Should().BeEmpty();
        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        events.Should().ContainSingle(stateEvent =>
            stateEvent.EventData.Is(NyxIdChatConversationRegistrationAcceptedEvent.Descriptor));
        events.Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(NyxIdChatConversationRegistrationUnavailableEvent.Descriptor));
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenRegistrationIsNotVisible_ShouldNotPrepareHistoryInitialization()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var dispatch = new RecordingSelfDispatchPort();
        using var provider = BuildServiceProvider(registry, new RecordingActorRuntime());
        const string actorId = "nyxid-chat-history-registration-unavailable";
        var agent = CreateConversationAgent(provider, actorId, dispatch);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = false,
        }));

        agent.State.PendingHistoryInitialization.Should().BeNull();
        agent.State.HistoryInitializationOperationId.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldRetryRegistrationBeforeStartingFirstTurn()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var dispatch = new RecordingSelfDispatchPort();
        using var provider = BuildServiceProvider(registry, new RecordingActorRuntime());
        const string actorId = "nyxid-chat-registration-retry";
        var agent = CreateConversationAgent(provider, actorId, dispatch);
        var command = new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
            FirstTurn = new NyxIdChatStartTurnCommand
            {
                ScopeId = "scope-a",
                ConversationActorId = actorId,
                TurnId = "turn-retry",
                TaskId = "task-retry",
                ClientRequestId = "client-retry",
                CommandId = "command-retry",
                CorrelationId = "correlation-retry",
                Prompt = "retry",
                ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
                {
                    Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
                    {
                        OwnerSubject = "owner-alpha",
                    },
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope(actorId, command));
        registry.RegisterStage = GAgentActorRegistryCommandStage.AdmissionVisible;
        await agent.HandleEventAsync(CreateEnvelope(actorId, command.Clone()));

        registry.RegisteredActors.Should().HaveCount(2);
        agent.State.ActiveTurn.TurnId.Should().Be("turn-retry");
    }

    [Fact]
    public async Task HistoryInitializationDispatch_ShouldIgnoreStaleSignalThenClearMatchingOutbox()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        var history = new RecordingChatHistoryCommandPort();
        var dispatch = new RecordingSelfDispatchPort();
        using var provider = BuildServiceProvider(
            registry,
            new RecordingActorRuntime(),
            history);
        const string actorId = "nyxid-chat-history-dispatch";
        var agent = CreateConversationAgent(provider, actorId, dispatch);
        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
        }));
        var pending = agent.State.PendingHistoryInitialization.Clone();

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatHistoryInitializationDispatchRequested
            {
                OperationId = "stale-operation",
                Attempt = pending.Attempt,
            }));

        history.Initializations.Should().BeEmpty();
        agent.State.PendingHistoryInitialization.ToByteString().Should().Equal(pending.ToByteString());

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatHistoryInitializationDispatchRequested
            {
                OperationId = pending.OperationId,
                Attempt = pending.Attempt,
            }));

        var initialization = history.Initializations.Should().ContainSingle().Which;
        initialization.OperationId.Should().Be(pending.OperationId);
        initialization.ScopeId.Should().Be("scope-a");
        initialization.ConversationId.Should().Be(actorId);
        initialization.ServiceId.Should().Be(actorId);
        initialization.ServiceKind.Should().Be(NyxIdChatServiceDefaults.GAgentKind);
        agent.State.PendingHistoryInitialization.Should().BeNull();
        agent.State.HistoryInitializationOperationId.Should().Be(pending.OperationId);

        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        var dispatched = events.Should().ContainSingle(stateEvent =>
                stateEvent.EventData.Is(NyxIdChatHistoryInitializationDispatchedEvent.Descriptor))
            .Which.EventData.Unpack<NyxIdChatHistoryInitializationDispatchedEvent>();
        dispatched.OperationId.Should().Be(pending.OperationId);
        dispatched.Attempt.Should().Be(1);
    }

    [Fact]
    public async Task HistoryInitializationDispatch_WhenPortFails_ShouldRetainOutboxAndScheduleStableRetry()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        var history = new RecordingChatHistoryCommandPort
        {
            InitializeException = new InvalidOperationException("history unavailable with bearer-secret"),
        };
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var dispatch = new RecordingSelfDispatchPort();
        using var provider = BuildServiceProvider(
            registry,
            new RecordingActorRuntime(),
            history,
            callbackScheduler: scheduler);
        const string actorId = "nyxid-chat-history-retry";
        var agent = CreateConversationAgent(provider, actorId, dispatch);
        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
        }));
        var pending = agent.State.PendingHistoryInitialization.Clone();

        await agent.HandleEventAsync(CreateEnvelope(
            actorId,
            new NyxIdChatHistoryInitializationDispatchRequested
            {
                OperationId = pending.OperationId,
                Attempt = pending.Attempt,
            }));

        history.Initializations.Should().ContainSingle();
        agent.State.PendingHistoryInitialization.Should().NotBeNull();
        agent.State.PendingHistoryInitialization.OperationId.Should().Be(pending.OperationId);
        agent.State.PendingHistoryInitialization.Attempt.Should().Be(2);

        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        var retry = events.Should().ContainSingle(stateEvent =>
                stateEvent.EventData.Is(NyxIdChatHistoryInitializationRetryScheduledEvent.Descriptor))
            .Which.EventData.Unpack<NyxIdChatHistoryInitializationRetryScheduledEvent>();
        retry.OperationId.Should().Be(pending.OperationId);
        retry.Attempt.Should().Be(2);
        retry.FailureCode.Should().Be("history_initialization_dispatch_failed");
        retry.ToString().Should().NotContain("bearer-secret");

        var timeout = scheduler.TimeoutRequests.Should().ContainSingle().Which;
        timeout.ActorId.Should().Be(actorId);
        timeout.DueTime.Should().BePositive();
        var retrySignal = timeout.TriggerEnvelope.Payload
            .Unpack<NyxIdChatHistoryInitializationDispatchRequested>();
        retrySignal.OperationId.Should().Be(pending.OperationId);
        retrySignal.Attempt.Should().Be(2);
        NyxIdChatHistoryInitializationDispatchRequested.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Equal("operation_id", "attempt");
        timeout.TriggerEnvelope.ToString().Should()
            .NotContain("scope-a")
            .And.NotContain("bearer-secret");
    }

    [Fact]
    public async Task ActivateAsync_WithPendingHistoryInitialization_ShouldRepublishTypedSelfSignal()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        var eventStore = new InMemoryEventStoreForTests();
        var initialDispatch = new RecordingSelfDispatchPort();
        using var provider = BuildServiceProvider(
            registry,
            new RecordingActorRuntime(),
            new RecordingChatHistoryCommandPort(),
            eventStore);
        const string actorId = "nyxid-chat-history-reactivation";
        var initialAgent = CreateConversationAgent(provider, actorId, initialDispatch);
        await initialAgent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
        }));
        var pending = initialAgent.State.PendingHistoryInitialization.Clone();

        var recoveryDispatch = new RecordingSelfDispatchPort();
        var recovered = CreateConversationAgent(provider, actorId, recoveryDispatch);
        await recovered.ActivateAsync();

        recovered.State.PendingHistoryInitialization.ToByteString().Should().Equal(pending.ToByteString());
        var signal = recoveryDispatch.Calls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatHistoryInitializationDispatchRequested>();
        signal.OperationId.Should().Be(pending.OperationId);
        signal.Attempt.Should().Be(pending.Attempt);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldNotAppendEquivalentBindingTwice()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        using var provider = BuildServiceProvider(registry, new RecordingActorRuntime());
        const string actorId = "nyxid-chat-profile-repeat";
        var agent = CreateConversationAgent(provider, actorId);
        var profile = BuildSealedProfile("profile-v1");

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
            AgentProfile = profile.Clone(),
        }));
        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
            AgentProfile = profile.Clone(),
        }));

        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        events.Count(static stateEvent => stateEvent.EventData.Is(AgentProfileBoundEvent.Descriptor))
            .Should()
            .Be(1);
        registry.RegisteredActors.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldRejectDifferentOrMissingProfileAfterBinding()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        using var provider = BuildServiceProvider(registry, new RecordingActorRuntime());
        const string actorId = "nyxid-chat-profile-conflict";
        var agent = CreateConversationAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            AgentProfile = BuildSealedProfile("profile-v1"),
        }));

        var replace = () => agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            AgentProfile = BuildSealedProfile("profile-v2"),
        }));
        var remove = () => agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
        }));

        await replace.Should().ThrowAsync<InvalidOperationException>();
        await remove.Should().ThrowAsync<InvalidOperationException>();
        registry.RegisteredActors.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldRejectInvalidDigestBeforeRegistryIo()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        using var provider = BuildServiceProvider(registry, new RecordingActorRuntime());
        const string actorId = "nyxid-chat-profile-bad-digest";
        var agent = CreateConversationAgent(provider, actorId);
        var profile = BuildSealedProfile("profile-v1");
        profile.ProfileVersion = "tampered";

        var act = () => agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            AgentProfile = profile,
        }));

        await act.Should().ThrowAsync<InvalidOperationException>();
        registry.RegisteredActors.Should().BeEmpty();
        (await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldRestoreBoundProfileFromCommittedEvents()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        using var provider = BuildServiceProvider(registry, new RecordingActorRuntime());
        const string actorId = "nyxid-chat-profile-restart";
        var first = CreateConversationAgent(provider, actorId);
        await first.ActivateAsync();
        await first.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            AgentProfile = BuildSealedProfile("profile-v1"),
        }));
        await first.DeactivateAsync();

        var restored = CreateConversationAgent(provider, actorId);
        await restored.ActivateAsync();

        restored.State.AgentProfile.Should().NotBeNull();
        restored.State.AgentProfile.ProfileVersion.Should().Be("profile-v1");
        AgentProfileSnapshotCodec.Verify(restored.State.AgentProfile).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, "complete snapshot")]
    [InlineData(true, "valid digest")]
    public async Task ActivateAsync_ShouldRejectCommittedBindingWithoutValidCompleteProfile(
        bool tamperDigest,
        string expectedMessage)
    {
        using var provider = BuildServiceProvider();
        var actorId = tamperDigest
            ? "nyxid-chat-profile-replay-invalid-digest"
            : "nyxid-chat-profile-replay-missing";
        var binding = new AgentProfileBoundEvent();
        if (tamperDigest)
        {
            var profile = BuildSealedProfile("profile-v1");
            profile.ProfileVersion = "tampered";
            binding.Profile = profile;
        }

        await AppendCommittedEventsAsync(provider, actorId, binding);
        var agent = CreateConversationAgent(provider, actorId);

        var act = () => agent.ActivateAsync();

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public async Task ActivateAsync_ShouldReplayEquivalentCommittedBindingIdempotently()
    {
        using var provider = BuildServiceProvider();
        const string actorId = "nyxid-chat-profile-replay-equivalent";
        var profile = BuildSealedProfile("profile-v1");
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = profile.Clone() },
            new AgentProfileBoundEvent { Profile = profile.Clone() });
        var agent = CreateConversationAgent(provider, actorId);

        await agent.ActivateAsync();

        AgentProfileSnapshotCodec.ByteEquivalent(agent.State.AgentProfile, profile).Should().BeTrue();
    }

    [Fact]
    public async Task ActivateAsync_ShouldRejectConflictingCommittedBindingDuringReplay()
    {
        using var provider = BuildServiceProvider();
        const string actorId = "nyxid-chat-profile-replay-conflict";
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedProfile("profile-v1") },
            new AgentProfileBoundEvent { Profile = BuildSealedProfile("profile-v2") });
        var agent = CreateConversationAgent(provider, actorId);

        var act = () => agent.ActivateAsync();

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be replaced*");
    }

    [Fact]
    public async Task Conversations_ShouldKeepIndependentProfileVersions()
    {
        var registry = new RecordingGAgentActorRegistryCommandPort();
        using var provider = BuildServiceProvider(registry, new RecordingActorRuntime());
        var agents = Enumerable.Range(1, 4)
            .Select(index => CreateConversationAgent(provider, $"nyxid-chat-profile-{index}"))
            .ToArray();

        for (var index = 0; index < agents.Length; index++)
        {
            await agents[index].HandleEventAsync(CreateEnvelope(agents[index].Id, new NyxIdChatConversationCreateCommand
            {
                ScopeId = "scope-a",
                AgentProfile = BuildSealedProfile($"profile-v{index + 1}"),
            }));
        }

        agents.Select(static agent => agent.State.AgentProfile.ProfileVersion)
            .Should()
            .Equal("profile-v1", "profile-v2", "profile-v3", "profile-v4");
    }

    [Fact]
    public void StoredChatMessage_ShouldExposeTypedTurnIdentity()
    {
        typeof(StoredChatMessage).GetProperty("TurnId").Should().NotBeNull();
    }

    [Fact]
    public async Task ActivateAsync_ShouldPinNyxIdProviderOnFirstInitialization()
    {
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
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
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
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

        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedProjectionPipeline_ShouldFlushLiveTextAndSnapshotEveryToolProtocol(
        bool emitTextToolCall)
    {
        const string actorId = "nyxid-chat-live-progress";
        const string sessionId = "turn-live-progress";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var services = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var provider = new ControlledProgressProviderFactory(emitTextToolCall);
        var tool = new ControlledProgressTool();
        var agent = CreateAgent(
            services,
            actorId,
            provider,
            [new StaticToolSource([tool])]);

        var streams = new InMemoryStreamProvider();
        var actorPublisher = new LocalActorPublisher(actorId, static () => null, static () => 0, streams);
        agent.EventPublisher = actorPublisher;
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetProperty("CommittedStateEventPublisher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(agent, actorPublisher);

        await using var responseBody = new FlushedSseFrameStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        var sseWriter = new NyxIdChatSseWriter(httpContext.Response);
        var aguiHub = new ProjectionSessionEventHub<AGUIEvent>(
            streams,
            new NyxIdChatSessionEventCodec());
        var projectionContext = new NyxIdChatSessionProjectionContext
        {
            RootActorId = actorId,
            SessionId = sessionId,
            ProjectionKind = "nyxid-chat-session",
        };
        var projector = new NyxIdChatSessionEventProjector(aguiHub);
        var committedPayloads = new List<Any>();

        await using var aguiSubscription = await aguiHub.SubscribeAsync(
            actorId,
            sessionId,
            async evt =>
            {
                _ = await NyxIdChatAguiSseEventWriter.WriteAsync(
                    evt,
                    sessionId,
                    sseWriter,
                    timeout.Token);
            },
            timeout.Token);
        await using var committedSubscription = await streams.GetStream(actorId).SubscribeAsync<EventEnvelope>(
            async envelope =>
            {
                if (CommittedStateEventEnvelope.TryGetObservedPayload(
                        envelope,
                        out var payload,
                        out _,
                        out _) && payload != null)
                {
                    committedPayloads.Add(payload.Clone());
                }

                await projector.ProjectAsync(projectionContext, envelope, timeout.Token);
            },
            timeout.Token);

        await agent.ActivateAsync(timeout.Token);
        var turnTask = agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "Use the controlled tool and answer.",
            SessionId = sessionId,
        });

        await provider.WaitingForFirstRoundRelease.Task.WaitAsync(timeout.Token);
        var observedFrames = new List<JsonObject>();
        var firstContent = await ReadUntilFrameAsync(
            responseBody.Frames,
            observedFrames,
            "TEXT_MESSAGE_CONTENT",
            timeout.Token);

        firstContent["textMessageContent"]!["delta"]!.GetValue<string>().Should().Be("first chunk");
        firstContent["sequence"]!.GetValue<long>().Should().BeGreaterThan(0);
        provider.FirstRoundReleased.Should().BeFalse();
        turnTask.IsCompleted.Should().BeFalse();

        provider.ReleaseFirstRound();
        var toolStart = await ReadUntilFrameAsync(
            responseBody.Frames,
            observedFrames,
            "TOOL_CALL_START",
            timeout.Token);
        await tool.Started.Task.WaitAsync(timeout.Token);

        toolStart["toolCallStart"]!["toolName"]!.GetValue<string>().Should().Be(tool.Name);
        toolStart["toolCallStart"]!["presentation"]!["displayName"]!
            .GetValue<string>().Should().Be("Controlled lookup");
        tool.Released.Should().BeFalse();
        turnTask.IsCompleted.Should().BeFalse();

        tool.Release("{\"ok\":true}");
        await turnTask.WaitAsync(timeout.Token);
        await ReadUntilFrameAsync(
            responseBody.Frames,
            observedFrames,
            "RUN_FINISHED",
            timeout.Token);

        var frameTypes = observedFrames.Select(FrameType).ToArray();
        frameTypes.Should().ContainInOrder(
            "TEXT_MESSAGE_START",
            "TEXT_MESSAGE_CONTENT",
            "TOOL_CALL_START",
            "TOOL_CALL_END",
            "TEXT_MESSAGE_CONTENT",
            "USAGE",
            "TEXT_MESSAGE_END",
            "RUN_FINISHED");
        frameTypes.Should().ContainSingle(type => type == "RUN_FINISHED");
        frameTypes.Should().NotContain("RUN_ERROR");

        var sequences = observedFrames
            .Select(frame => frame["sequence"]!.GetValue<long>())
            .ToArray();
        sequences.Should().BeInAscendingOrder();
        sequences.Should().OnlyHaveUniqueItems();
        sequences.Should().Equal(Enumerable.Range(1, sequences.Length).Select(static value => (long)value));

        var completionIndex = committedPayloads.FindIndex(payload =>
            payload.Is(RoleChatSessionCompletedEvent.Descriptor));
        completionIndex.Should().BeGreaterThanOrEqualTo(0);
        var completion = committedPayloads[completionIndex].Unpack<RoleChatSessionCompletedEvent>();
        completion.TerminalProgress.Should().ContainSingle(progress =>
            progress.PayloadCase == RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal);
        committedPayloads.Should().NotContain(payload =>
            payload.Is(RoleChatSessionProgressedEvent.Descriptor) &&
            payload.Unpack<RoleChatSessionProgressedEvent>().PayloadCase ==
            RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal);
        completion.ToolCalls.Should().ContainSingle();
        completion.ToolCalls[0].ToolName.Should().Be(tool.Name);
        completion.ToolCalls[0].Presentation.DisplayName.Should().Be("Controlled lookup");
        completion.ToolCalls[0].Presentation.BuiltIn.ToolId.Should().Be(tool.Name);
    }

    [Fact]
    public async Task HandleChatRequest_BoundTurn_ShouldPrepareAndMaterializeCatalogOnceEach()
    {
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var llm = new StreamingToolLoopProviderFactory(
        [
            [new LLMStreamChunk { DeltaContent = "done" }],
        ]);
        var registry = new CountingToolSetRegistry();
        var materializer = new AgentProfileTurnCatalogMaterializer(registry, new NoMatchClassifier());
        const string actorId = "nyxid-chat-catalog-bound";
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedProfile("profile-v1", "profile.route") });
        var agent = CreateAgent(provider, actorId, llm, turnCatalogMaterializer: materializer);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "catalog-bound-session",
        });

        registry.ResolveCount.Should().Be(2);
        llm.StreamRequests.Should().ContainSingle();
        llm.StreamRequests[0].ToolContext!.ToolVisibility.IsRestricted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChatRequest_DeadlineBeforeAuthorityBatch_ShouldPersistOnlyTimeoutTerminal()
    {
        const int timeoutMs = 1_000;
        const string actorId = "nyxid-chat-authority-pre-batch-cancel";
        const string sessionId = "authority-pre-batch-cancel-session";
        var timeProvider = new ManualDeadlineTimeProvider();
        var blockingSource = new ReleasableBlockingToolSource();
        var registry = new BlockingProfileToolSetRegistry("profile.route", blockingSource);
        var materializer = new AgentProfileTurnCatalogMaterializer(
            registry,
            new NoMatchClassifier(),
            timeProvider: timeProvider);
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedProfile("profile-v1", "profile.route") });
        var agent = CreateAgent(
            provider,
            actorId,
            new StreamingToolLoopProviderFactory([[new LLMStreamChunk { DeltaContent = "must not run" }]]),
            timeProvider: timeProvider,
            turnCatalogMaterializer: materializer);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var handling = agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "wait before authority batch",
            SessionId = sessionId,
            TimeoutMs = timeoutMs,
        });
        await blockingSource.Started;

        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await handling;

        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        events.Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionStartedEvent.Descriptor) ||
            stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor));
        var timeout = events
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle().Which;
        timeout.SessionId.Should().Be(sessionId);
        timeout.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        timeout.FailureCode.Should().Be("LLM_TIMEOUT");
        blockingSource.CancellationObserved.Should().BeTrue();
        publisher.Published.OfType<TextMessageStartEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleChatRequest_CancellationAfterAuthorityBatch_ShouldKeepCommittedFenceWithoutFailureReconcile()
    {
        const int timeoutMs = 1_000;
        const string actorId = "nyxid-chat-authority-post-batch-cancel";
        const string sessionId = "authority-post-batch-cancel-session";
        var timeProvider = new ManualDeadlineTimeProvider();
        var tools = new IAgentTool[]
        {
            new DelegateTool("recovery", _ => "recovered"),
            new DelegateTool("task", _ => "done"),
            new DelegateTool("hidden", _ => "hidden"),
        };
        var fetcher = new CancellationBlockingExactFetcher();
        var materializer = new AgentProfileTurnCatalogMaterializer(
            new StaticProfileToolSetRegistry("profile.route", tools),
            new NoMatchClassifier(),
            fetcher,
            timeProvider: timeProvider);
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedEnforcedProfile() });
        var llm = new StreamingToolLoopProviderFactory(
            [[new LLMStreamChunk { DeltaContent = "must not run" }]]);
        var agent = CreateAgent(
            provider,
            actorId,
            llm,
            [new StaticToolSource(tools)],
            timeProvider: timeProvider,
            turnCatalogMaterializer: materializer);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        var handling = agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "/alpha run",
            SessionId = sessionId,
            TimeoutMs = timeoutMs,
            ToolContext = new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload { NyxIdAccessToken = "turn-token" },
            },
        });
        await fetcher.Started;

        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await handling;

        fetcher.CancellationObserved.Should().BeTrue();
        llm.StreamRequests.Should().BeEmpty();
        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        var authorityEvents = events
            .Where(stateEvent => stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<AgentProfileTurnAuthorityCommittedEvent>())
            .ToArray();
        authorityEvents.Should().ContainSingle();
        authorityEvents[0].CommitKind.Should().Be(AgentProfileTurnAuthorityCommitKind.Initial);
        authorityEvents[0].Authority.ReconciliationKey.Should().BeEquivalentTo(
            new AgentProfileTurnReconciliationKey { SessionId = sessionId, Attempt = 1 });
        authorityEvents[0].Authority.DegradationReasons.Should().NotContain(
            AgentProfileTurnDegradationReason.MaterializationFailed);
        var completion = events
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Where(completed => completed.SessionId == sessionId)
            .Should().ContainSingle().Which;
        completion.Content.Should().Contain($"LLM request timed out after {timeoutMs}ms");
        completion.ContentEmitted.Should().BeFalse();
        publisher.Published.OfType<TextMessageStartEvent>()
            .Should().ContainSingle(start => start.SessionId == sessionId);
        publisher.Published.OfType<TextMessageEndEvent>()
            .Should().ContainSingle(end => end.SessionId == sessionId && end.Content == completion.Content);
    }

    [Fact]
    public async Task HandleChatRequest_BoundTurn_ShouldPropagateTokenCatalogPromptAndAdmission()
    {
        const string turnToken = "turn-token-alpha";
        var hiddenExecuteCount = 0;
        var tools = new IAgentTool[]
        {
            new DelegateTool("recovery", _ => "recovered"),
            new DelegateTool("task", _ => "task complete"),
            new DelegateTool("hidden", _ =>
            {
                hiddenExecuteCount++;
                return "must not execute";
            }),
        };
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var llm = new StreamingToolLoopProviderFactory(
        [
            [new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "forged-hidden-call",
                    Name = "hidden",
                    ArgumentsJson = "{}",
                },
            }],
            [new LLMStreamChunk { DeltaContent = "done" }],
        ]);
        var registry = new StaticProfileToolSetRegistry("profile.route", tools);
        var fetcher = new RecordingExactFetcher(ExactRemoteSkillFetchResult.Success(
            ExactSkillGuid,
            ExactSkillVersion,
            ExactSkillName,
            ExactSkillPublisher,
            ExactSkillSha256,
            "---\nname: skill-alpha\n---\nSelected turn instructions."));
        var materializer = new AgentProfileTurnCatalogMaterializer(
            registry,
            new NoMatchClassifier(),
            fetcher);
        const string actorId = "nyxid-chat-catalog-success";
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedEnforcedProfile() });
        var agent = CreateAgent(
            provider,
            actorId,
            llm,
            [new StaticToolSource(tools)],
            turnCatalogMaterializer: materializer);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "/alpha run",
            SessionId = "catalog-success-session",
            ToolContext = new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload { NyxIdAccessToken = turnToken },
            },
        });

        registry.ResolveCount.Should().Be(2);
        fetcher.CallCount.Should().Be(1);
        fetcher.AccessToken.Should().Be(turnToken);
        fetcher.SkillRef.Should().BeEquivalentTo(new ExactRemoteSkillRef
        {
            Guid = ExactSkillGuid,
            LiteralVersion = ExactSkillVersion,
        });
        llm.StreamRequests.Should().HaveCount(2);
        var firstRequest = llm.StreamRequests[0];
        firstRequest.Tools!.Select(static tool => tool.Name).Should().BeEquivalentTo("recovery", "task");
        firstRequest.ToolContext!.ToolVisibility.Allows("hidden").Should().BeFalse();
        firstRequest.Messages.Single(static message => message.Role == "system").Content
            .Should().Contain("Selected turn instructions.");
        hiddenExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleChatRequest_ShadowTurn_ShouldObserveRouteWithoutChangingLegacyExecution()
    {
        var tools = new IAgentTool[]
        {
            new DelegateTool("recovery", _ => "recovered"),
            new DelegateTool("task", _ => "task complete"),
            new DelegateTool("legacy", _ => "legacy complete"),
        };
        var registry = new StaticProfileToolSetRegistry("profile.route", tools);
        var fetcher = new RecordingExactFetcher(ExactRemoteSkillFetchResult.Success(
            ExactSkillGuid,
            ExactSkillVersion,
            ExactSkillName,
            ExactSkillPublisher,
            ExactSkillSha256,
            "---\nname: skill-alpha\n---\nSelected turn instructions."));
        var materializer = new AgentProfileTurnCatalogMaterializer(
            registry,
            new NoMatchClassifier(),
            fetcher);
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        const string actorId = "nyxid-chat-shadow-zero-side-effects";
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedShadowProfile() });
        var llm = new StreamingToolLoopProviderFactory(
        [
            [new LLMStreamChunk { DeltaContent = "done" }],
        ]);
        var agent = CreateAgent(
            provider,
            actorId,
            llm,
            [new StaticToolSource(tools)],
            turnCatalogMaterializer: materializer);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "/alpha run",
            SessionId = "shadow-zero-side-effects-session",
            ToolContext = new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload { NyxIdAccessToken = "turn-token" },
            },
        });

        registry.ResolveCount.Should().Be(1);
        fetcher.CallCount.Should().Be(0);
        llm.StreamRequests.Should().ContainSingle();
        llm.StreamRequests[0].ToolContext!.ToolVisibility.IsRestricted.Should().BeFalse();
        llm.StreamRequests[0].Messages.Single(static message => message.Role == "system").Content
            .Should().NotContain("Agent profile:").And.NotContain("Selected turn instructions.");
        var events = await provider.GetRequiredService<IEventStore>().GetEventsAsync(actorId);
        events.Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(AgentProfileTurnAuthorityCommittedEvent.Descriptor));
    }

    [Fact]
    public async Task HandleChatRequest_EnforcedTurn_ShouldRecordFiveRealTelemetrySeams()
    {
        var seams = new List<string>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentProfileTelemetry.MeterName &&
                instrument.Name == "aevatar.agent_profile.seam.events")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "aevatar.agent_profile.seam" && tag.Value?.ToString() is { } seam)
                    seams.Add(seam);
            }
        });
        meterListener.Start();

        var tools = new IAgentTool[]
        {
            new DelegateTool("recovery", _ => "recovered"),
            new DelegateTool("task", _ => "task complete"),
        };
        var materializer = new AgentProfileTurnCatalogMaterializer(
            new StaticProfileToolSetRegistry("profile.route", tools),
            new NoMatchClassifier(),
            new RecordingExactFetcher(ExactRemoteSkillFetchResult.Success(
                ExactSkillGuid,
                ExactSkillVersion,
                ExactSkillName,
                ExactSkillPublisher,
                ExactSkillSha256,
                "---\nname: skill-alpha\n---\nSelected turn instructions.")));
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        const string actorId = "nyxid-chat-five-telemetry-seams";
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedEnforcedProfile() });
        var agent = CreateAgent(
            provider,
            actorId,
            new StreamingToolLoopProviderFactory([[new LLMStreamChunk { DeltaContent = "done" }]]),
            [new StaticToolSource(tools)],
            turnCatalogMaterializer: materializer);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "/alpha run",
            SessionId = "five-telemetry-seams-session",
            ToolContext = new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload { NyxIdAccessToken = "turn-token" },
            },
        });

        seams.Should().Contain(["route", "exact_fetch", "materialize", "plan_handoff", "first_stream_output"]);
    }

    [Fact]
    public async Task HandleChatRequest_BoundTurnWithoutMaterializer_ShouldRejectAllTools()
    {
        await AssertBoundTurnMaterializationFailureRejectsAllToolsAsync(
            turnCatalogMaterializer: null,
            "nyxid-chat-catalog-materializer-missing");
    }

    [Fact]
    public async Task HandleChatRequest_BoundTurnWhenMaterializerThrows_ShouldRejectAllTools()
    {
        var materializer = new AgentProfileTurnCatalogMaterializer(
            new ThrowingNameToolSetRegistry(),
            new NoMatchClassifier());

        await AssertBoundTurnMaterializationFailureRejectsAllToolsAsync(
            materializer,
            "nyxid-chat-catalog-materializer-throws");
    }

    [Fact]
    public async Task HandleChatRequest_UnboundTurn_ShouldNotMaterializeCatalog()
    {
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var llm = new StreamingToolLoopProviderFactory(
        [
            [new LLMStreamChunk { DeltaContent = "done" }],
        ]);
        var registry = new CountingToolSetRegistry();
        var materializer = new AgentProfileTurnCatalogMaterializer(registry, new NoMatchClassifier());
        var agent = CreateAgent(
            provider,
            "nyxid-chat-catalog-unbound",
            llm,
            turnCatalogMaterializer: materializer);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "catalog-unbound-session",
        });

        registry.ResolveCount.Should().Be(0);
        llm.StreamRequests.Should().ContainSingle();
        llm.StreamRequests[0].ToolContext!.ToolVisibility.IsRestricted.Should().BeFalse();
    }

    [Fact]
    public async Task HandleChatRequest_CompletedReplay_ShouldNotRematerializeCatalog()
    {
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var llm = new StreamingToolLoopProviderFactory(
        [
            [new LLMStreamChunk { DeltaContent = "done" }],
        ]);
        var registry = new CountingToolSetRegistry();
        var materializer = new AgentProfileTurnCatalogMaterializer(registry, new NoMatchClassifier());
        const string actorId = "nyxid-chat-catalog-replay";
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedProfile("profile-v1", "profile.route") });
        var agent = CreateAgent(provider, actorId, llm, turnCatalogMaterializer: materializer);
        var request = new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "catalog-replay-session",
        };

        await agent.ActivateAsync();
        await agent.HandleChatRequest(request);
        await agent.HandleChatRequest(request.Clone());

        registry.ResolveCount.Should().Be(2);
        llm.StreamRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ActivateAsync_ShouldUseConfiguredRelayCallbackUrlInSystemPrompt()
    {
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
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
        systemPrompt.Should().Contain("produce the final text reply directly");
        systemPrompt.Should().Contain("The channel runtime will deliver it through the relay reply token");
        systemPrompt.Should().NotContain("lark_messages_reply");
        systemPrompt.Should().NotContain("lark_messages_react");
        systemPrompt.Should().NotContain("call `lark_messages_react` first");
    }

    [Fact]
    public async Task HandleChatRequest_ShouldSaveDirectChatTurnToHistory()
    {
        var history = new RecordingChatHistoryCommandPort();
        var now = DateTimeOffset.Parse("2026-06-11T01:02:03Z", null, System.Globalization.DateTimeStyles.AssumeUniversal);
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk
                    {
                        DeltaContent = "direct answer",
                        Usage = new TokenUsage(3, 5, 8),
                    },
                ],
            ]);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-history",
            llmProviderFactory,
            timeProvider: new FixedTimeProvider(now),
            loopbackHistoryDelivery: true);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "How do I connect a bot?",
            SessionId = "session-history",
        });

        history.Saved.Should().ContainSingle();
        var saved = history.Saved.Single();
        saved.ScopeId.Should().Be("scope-a");
        saved.ConversationId.Should().Be("nyxid-chat-history");
        saved.Meta.Should().BeEquivalentTo(new ConversationMeta(
            "nyxid-chat-history",
            "How do I connect a bot?",
            "nyxid-chat-history",
            NyxIdChatServiceDefaults.GAgentKind,
            now,
            now,
            2,
            NyxIdChatServiceDefaults.ProviderName,
            null));
        saved.Messages.Should().HaveCount(2);
        saved.Messages[0].Should().BeEquivalentTo(new StoredChatMessage(
            "session-history-user",
            "user",
            "How do I connect a bot?",
            now.ToUnixTimeMilliseconds(),
            "completed",
            null,
            null,
            null,
            null,
            "session-history"));
        saved.Messages[1].Should().BeEquivalentTo(new StoredChatMessage(
            "session-history-assistant",
            "assistant",
            "direct answer",
            now.ToUnixTimeMilliseconds(),
            "completed",
            null,
            null,
            null,
            null,
            "session-history"));
    }

    [Fact]
    public async Task Activation_ShouldRetryPendingDirectChatHistoryDelivery_Idempotently()
    {
        var store = new InMemoryEventStoreForTests();
        var history = new RecordingChatHistoryCommandPort
        {
            SaveException = new InvalidOperationException("history unavailable"),
        };
        using var provider = BuildServiceProvider(historyCommandPort: history, eventStore: store);
        const string actorId = "nyxid-chat-history-activation-retry";
        var first = CreateAgent(
            provider,
            actorId,
            new StreamingToolLoopProviderFactory(
                [[new LLMStreamChunk { DeltaContent = "durable answer" }]]),
            loopbackHistoryDelivery: true);

        await first.ActivateAsync();
        await first.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "persist this",
            SessionId = "turn-history-retry",
        });

        first.State.Sessions["turn-history-retry"].HistoryDeliveryStatus.Should()
            .Be(RoleChatHistoryDeliveryStatus.Prepared);
        history.Saved.Should().BeEmpty();
        await first.DeactivateAsync();

        history.SaveException = null;
        var recovered = CreateAgent(provider, actorId, loopbackHistoryDelivery: true);
        await recovered.ActivateAsync();

        recovered.State.Sessions["turn-history-retry"].HistoryDeliveryStatus.Should()
            .Be(RoleChatHistoryDeliveryStatus.Dispatched);
        recovered.State.Sessions["turn-history-retry"].HistoryDeliveryAttempt.Should().Be(1);
        history.Saved.Should().ContainSingle().Which.Messages.Should()
            .OnlyContain(static message => message.TurnId == "turn-history-retry");
        (await store.GetEventsAsync(actorId))
            .Count(stateEvent => stateEvent.EventData.Is(NyxIdDirectChatHistoryDispatchedEvent.Descriptor))
            .Should().Be(1);
    }

    [Theory]
    [InlineData(RoleChatSessionOutcome.Completed, "completed")]
    [InlineData(RoleChatSessionOutcome.Failed, "error")]
    public async Task DirectChatHistory_ShouldReconcileDeliveredOutcomeUncertainWithStableIdentity(
        RoleChatSessionOutcome reconciledOutcome,
        string expectedStatus)
    {
        var store = new InMemoryEventStoreForTests();
        var history = new RecordingChatHistoryCommandPort();
        using var services = BuildServiceProvider(historyCommandPort: history, eventStore: store);
        var actorId = $"nyxid-chat-history-reconciliation-{expectedStatus}";
        const string sessionId = "turn-history-reconciliation";
        await AppendCommittedEventsAsync(
            services,
            actorId,
            new RoleChatSessionStartedEvent
            {
                SessionId = sessionId,
                ScopeId = "scope-a",
                Prompt = "perform side effect",
            },
            new RoleChatSessionProgressedEvent
            {
                SessionId = sessionId,
                Sequence = 1,
                ToolStarted = new RoleChatToolStartedProgress
                {
                    CallId = "call-side-effect",
                    ToolName = "side_effecting_tool",
                },
            });
        var first = CreateAgent(services, actorId, loopbackHistoryDelivery: true);
        await first.ActivateAsync();

        await first.HandleIncompleteSessionFinalizationRequestedAsync(
            new RoleChatIncompleteSessionFinalizationRequested
            {
                SessionId = sessionId,
                ExpectedLastProgressSequence = 1,
            });

        var firstSession = first.State.Sessions[sessionId];
        firstSession.HistoryDeliveryStatus.Should().Be(RoleChatHistoryDeliveryStatus.Dispatched);
        firstSession.HistoryDeliveryAttempt.Should().Be(1);
        var deliveryId = firstSession.HistoryDeliveryId;
        var uncertainAssistant = history.Saved.Should().ContainSingle().Which.Messages[1];
        uncertainAssistant.Status.Should().Be("outcome_uncertain");
        uncertainAssistant.Content.Should().Contain("outcome could not be confirmed");
        await first.DeactivateAsync();

        var version = await store.GetVersionAsync(actorId);
        await store.AppendAsync(
            actorId,
            [StateEventFor(actorId, version + 1, new RoleChatSessionCompletedEvent
            {
                SessionId = sessionId,
                Prompt = "perform side effect",
                Content = reconciledOutcome == RoleChatSessionOutcome.Completed ? "confirmed result" : string.Empty,
                Outcome = reconciledOutcome,
                FailureCode = reconciledOutcome == RoleChatSessionOutcome.Failed ? "CONFIRMED_FAILURE" : string.Empty,
                SafeMessage = reconciledOutcome == RoleChatSessionOutcome.Failed ? "The operation failed." : string.Empty,
                TerminalTime = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-02T08:00:00Z")),
            })],
            expectedVersion: version);
        var recovered = CreateAgent(services, actorId, loopbackHistoryDelivery: true);

        await recovered.ActivateAsync();

        var reconciledSession = recovered.State.Sessions[sessionId];
        reconciledSession.HistoryDeliveryStatus.Should().Be(RoleChatHistoryDeliveryStatus.Dispatched);
        reconciledSession.HistoryDeliveryAttempt.Should().Be(2);
        reconciledSession.HistoryDeliveryId.Should().Be(deliveryId);
        history.Saved.Should().HaveCount(2);
        history.Saved.SelectMany(saved => saved.Messages).Should()
            .OnlyContain(message => message.TurnId == sessionId);
        history.Saved[0].Messages.Select(message => message.Id).Should()
            .Equal(history.Saved[1].Messages.Select(message => message.Id));
        history.Saved[1].Messages[1].Status.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task PendingDirectChatHistoryOutbox_ShouldRemainNonTrimmableAtAdmissionCapacity()
    {
        var store = new InMemoryEventStoreForTests();
        using var services = BuildServiceProvider(
            historyCommandPort: new RecordingChatHistoryCommandPort(),
            eventStore: store);
        const string actorId = "nyxid-chat-history-capacity";
        var events = new List<IMessage>
        {
            new RoleChatSessionStartedEvent
            {
                SessionId = "history-pending",
                ScopeId = "scope-a",
                Prompt = "preserve history",
            },
            new RoleChatSessionCompletedEvent
            {
                SessionId = "history-pending",
                Prompt = "preserve history",
                Content = "durable answer",
                Outcome = RoleChatSessionOutcome.Completed,
            },
        };
        events.AddRange(Enumerable.Range(1, 127).Select(index => (IMessage)new RoleChatSessionStartedEvent
        {
            SessionId = $"incomplete-{index}",
            Prompt = $"prompt-{index}",
        }));
        await AppendCommittedEventsAsync(services, actorId, events.ToArray());
        var llm = new StreamingToolLoopProviderFactory(
            [[new LLMStreamChunk { DeltaContent = "must not run" }]]);
        var agent = CreateAgent(services, actorId, llm);
        var publisher = new RecordingEventPublisher { FailHistoryDeliveryRequests = true };
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            SessionId = "capacity-overflow",
            CommandAttemptId = "capacity-attempt",
            Prompt = "reject this",
        });

        agent.State.Sessions.Should().HaveCount(128);
        agent.State.Sessions.Should().ContainKey("history-pending");
        agent.State.Sessions["history-pending"].HistoryDeliveryStatus.Should()
            .Be(RoleChatHistoryDeliveryStatus.Prepared);
        agent.State.Sessions.Should().NotContainKey("capacity-overflow");
        llm.StreamRequests.Should().BeEmpty();
        (await store.GetEventsAsync(actorId))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatCommandAttemptRejectedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatCommandAttemptRejectedEvent>())
            .Should().ContainSingle(rejection =>
                rejection.RequestedSessionId == "capacity-overflow" &&
                rejection.Reason == RoleChatCommandAttemptRejectionReason.CapacityExhausted);
    }

    [Fact]
    public async Task HistoryRequestPublishFailure_ShouldNotPolluteCommittedTerminalOrActivation()
    {
        var store = new InMemoryEventStoreForTests();
        var history = new RecordingChatHistoryCommandPort();
        using var services = BuildServiceProvider(historyCommandPort: history, eventStore: store);
        const string actorId = "nyxid-chat-history-request-failure";
        var first = CreateAgent(
            services,
            actorId,
            new StreamingToolLoopProviderFactory(
                [[new LLMStreamChunk { DeltaContent = "durable answer" }]]));
        var firstPublisher = new RecordingEventPublisher { FailHistoryDeliveryRequests = true };
        first.EventPublisher = firstPublisher;

        await first.ActivateAsync();
        await first.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "persist this",
            SessionId = "turn-history-publish-failure",
            RunContext = new RoleChatRunContext
            {
                RunId = "run-history-publish-failure",
                CommandId = "command-history-publish-failure",
                CorrelationId = "correlation-history-publish-failure",
                CompletionNotificationActorId = "service-run:scope-a:service-a:run-history-publish-failure",
            },
        });

        first.State.Sessions["turn-history-publish-failure"].Completed.Should().BeTrue();
        first.State.Sessions["turn-history-publish-failure"].HistoryDeliveryStatus.Should()
            .Be(RoleChatHistoryDeliveryStatus.Prepared);
        first.State.Sessions["turn-history-publish-failure"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
        firstPublisher.Published.OfType<NyxIdDirectChatHistoryDeliveryRequested>().Should().ContainSingle();
        firstPublisher.Published.OfType<RoleChatSessionCompletedEvent>().Should().ContainSingle();
        history.Saved.Should().BeEmpty();
        await first.DeactivateAsync();

        var recovered = CreateAgent(services, actorId);
        var recoveredPublisher = new RecordingEventPublisher { FailHistoryDeliveryRequests = true };
        recovered.EventPublisher = recoveredPublisher;

        await recovered.ActivateAsync();

        recovered.State.Sessions["turn-history-publish-failure"].HistoryDeliveryStatus.Should()
            .Be(RoleChatHistoryDeliveryStatus.Prepared);
        recoveredPublisher.Published.OfType<NyxIdDirectChatHistoryDeliveryRequested>().Should().ContainSingle();
        history.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleChatRequest_DifferentTurnsOnSameActor_ShouldShareHistoryAndArchiveTurnIds()
    {
        var history = new RecordingChatHistoryCommandPort();
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [new LLMStreamChunk { DeltaContent = "first answer" }],
                [new LLMStreamChunk { DeltaContent = "second answer" }],
            ]);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-multi-turn",
            llmProviderFactory,
            loopbackHistoryDelivery: true);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "first prompt",
            SessionId = "turn-first",
        });
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "second prompt",
            SessionId = "turn-second",
        });

        llmProviderFactory.StreamRequests.Should().HaveCount(2);
        llmProviderFactory.StreamRequests[1].Messages
            .Where(static message => message.Role != "system")
            .Select(static message => (message.Role, message.Content))
            .Should()
            .ContainInOrder(
                ("user", "first prompt"),
                ("assistant", "first answer"),
                ("user", "second prompt"));
        agent.State.Sessions["turn-first"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        agent.State.Sessions["turn-second"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);

        history.Saved.Should().HaveCount(2);
        history.Saved[0].ConversationId.Should().Be("nyxid-chat-multi-turn");
        history.Saved[1].ConversationId.Should().Be("nyxid-chat-multi-turn");
        history.Saved[0].Messages.Select(static message => message.Id)
            .Should().Equal("turn-first-user", "turn-first-assistant");
        history.Saved[1].Messages.Select(static message => message.Id)
            .Should().Equal("turn-second-user", "turn-second-assistant");
        history.Saved[0].Messages.Should().OnlyContain(static message => message.TurnId == "turn-first");
        history.Saved[1].Messages.Should().OnlyContain(static message => message.TurnId == "turn-second");
    }

    [Fact]
    public async Task HandleChatRequest_ReplayedTurn_ShouldReuseTerminalHistoryAndContinueLaterTurn()
    {
        var history = new RecordingChatHistoryCommandPort();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-20T01:02:03Z"));
        using var services = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call-once",
                            Name = "count_once",
                            ArgumentsJson = "{}",
                        },
                    },
                ],
                [new LLMStreamChunk { DeltaContent = "first answer" }],
                [new LLMStreamChunk { DeltaContent = "later answer" }],
            ]);
        var toolCallCount = 0;
        var agent = CreateAgent(
            services,
            "nyxid-chat-idempotent-history",
            llmProviderFactory,
            [new StaticToolSource([new DelegateTool("count_once", _ =>
            {
                toolCallCount++;
                return "ok";
            })])],
            timeProvider: clock);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();
        var replayedRequest = new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "first prompt",
            SessionId = "turn-client-request-1",
        };
        await agent.HandleChatRequest(replayedRequest);
        await DeliverPublishedHistoryRequestsAsync(agent, publisher);
        var providerCallsAfterFirstTurn = llmProviderFactory.StreamRequests.Count;
        clock.Advance(TimeSpan.FromMinutes(1));

        await agent.HandleChatRequest(replayedRequest.Clone());

        llmProviderFactory.StreamRequests.Should().HaveCount(providerCallsAfterFirstTurn);
        toolCallCount.Should().Be(1);
        publisher.Published.OfType<TextMessageEndEvent>()
            .Should().HaveCount(2)
            .And.OnlyContain(evt => evt.SessionId == "turn-client-request-1");
        history.Saved.Should().ContainSingle();
        history.Saved[0].Messages.Should().OnlyContain(static message => message.TurnId == "turn-client-request-1");

        clock.Advance(TimeSpan.FromMinutes(1));
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "later prompt",
            SessionId = "turn-client-request-2",
        });
        await DeliverPublishedHistoryRequestsAsync(agent, publisher);

        llmProviderFactory.StreamRequests.Should().HaveCount(providerCallsAfterFirstTurn + 1);
        llmProviderFactory.StreamRequests[^1].Messages
            .Where(static message => message.Role != "system")
            .Select(static message => (message.Role, message.Content))
            .Should().ContainInOrder(
                ("user", "first prompt"),
                ("assistant", "first answer"),
                ("user", "later prompt"));
        toolCallCount.Should().Be(1);
        agent.State.MessageCount.Should().Be(2);
        history.Saved.Should().HaveCount(2);
        history.Saved[^1].Messages.Should().OnlyContain(static message => message.TurnId == "turn-client-request-2");
    }

    private static async Task DeliverPublishedHistoryRequestsAsync(
        NyxIdChatGAgent agent,
        RecordingEventPublisher publisher)
    {
        foreach (var request in publisher.Published.OfType<NyxIdDirectChatHistoryDeliveryRequested>())
            await agent.HandleDirectChatHistoryDeliveryRequestedAsync(request);
    }

    [Fact]
    public async Task HandleChatRequest_DisconnectedService_ShouldArchiveBlockerAndAdmitNextTurn()
    {
        var history = new RecordingChatHistoryCommandPort();
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call-auth",
                            Name = "nyxid_require_service",
                            ArgumentsJson =
                                """{"service_slug":"api-github","resource_uri":"/repos/private?access_token=query-secret#credential=fragment-secret"}""",
                        },
                    },
                ],
                [new LLMStreamChunk { DeltaContent = "follow-up answer" }],
            ]);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-blocked-history",
            llmProviderFactory,
            [new StaticToolSource([new VerifiedMissingServiceTool()])],
            loopbackHistoryDelivery: true);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "read private repository",
            SessionId = "turn-blocked",
        });
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "ordinary follow-up",
            SessionId = "turn-after-block",
        });

        llmProviderFactory.StreamRequests.Should().HaveCount(2);
        llmProviderFactory.StreamRequests[1].Messages.Should().Contain(message =>
            message.Role == "user" && message.Content == "read private repository");
        var replayedToolMessages = llmProviderFactory.StreamRequests[1].Messages;
        var replayedAssistant = replayedToolMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        replayedAssistant.ToolCalls![0].Id.Should().Be("call-auth");
        replayedAssistant.ToolCalls[0].Name.Should().Be("nyxid_require_service");
        replayedAssistant.ToolCalls[0].ArgumentsJson.Should()
            .NotContain("query-secret")
            .And.NotContain("fragment-secret");
        replayedAssistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        replayedToolMessages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-auth");
        replayedToolMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments =>
                arguments.Contains("query-secret", StringComparison.Ordinal) ||
                arguments.Contains("fragment-secret", StringComparison.Ordinal));
        agent.State.Sessions["turn-blocked"].Outcome.Should().Be(RoleChatSessionOutcome.Blocked);
        agent.State.Sessions["turn-blocked"].ToolCalls.Should().ContainSingle(call =>
            call.CallId == "call-auth" &&
            call.ToolName == "nyxid_require_service" &&
            call.ArgumentsJson == string.Empty);
        agent.State.Sessions["turn-blocked"].ToolReceipts
            .Should()
            .OnlyContain(receipt =>
                !receipt.ToString().Contains("query-secret", StringComparison.Ordinal) &&
                !receipt.ToString().Contains("fragment-secret", StringComparison.Ordinal));
        agent.State.Sessions["turn-after-block"].Outcome.Should().Be(RoleChatSessionOutcome.Completed);

        history.Saved.Should().HaveCount(2);
        var blockedAssistant = history.Saved[0].Messages.Should()
            .ContainSingle(static message => message.Role == "assistant").Which;
        blockedAssistant.Id.Should().Be("turn-blocked-assistant");
        blockedAssistant.Status.Should().Be("blocked");
        blockedAssistant.Error.Should().Be(
            "No caller-visible NyxID UserService matches the requested service.");
        blockedAssistant.ToString().Should().NotContain("bearer-secret").And.NotContain("credential");
        history.Saved[1].Messages.Should().ContainSingle(message =>
            message.Role == "assistant" &&
            message.Status == "completed" &&
            message.Content == "follow-up answer");
    }

    [Fact]
    public async Task HandleChatRequest_NyxId401_ShouldCommitAndProjectCredentialFreeAuthorizationBlocker()
    {
        const string actorId = "nyxid-chat-real-unauthorized";
        var eventStore = new InMemoryEventStoreForTests();
        var history = new RecordingChatHistoryCommandPort();
        using var services = BuildServiceProvider(historyCommandPort: history, eventStore: eventStore);
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(new FixedNyxIdResponseHandler(
                HttpStatusCode.Unauthorized,
                """{"error":"unauthorized","error_code":1001,"message":"expired bearer-secret"}""")));
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-unauthorized",
                        Name = "nyxid_proxy",
                        ArgumentsJson =
                            """{"service_id":"us-github-alpha","slug":"api-github","path":"/repos/private?access_token=query-secret","headers":{"X-Credential":"header-secret"}}""",
                    },
                }],
                [new LLMStreamChunk { DeltaContent = "later answer" }],
            ]);
        var agent = CreateAgent(
            services,
            actorId,
            llmProviderFactory,
            [new StaticToolSource([new NyxIdProxyTool(client)])]);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "read private repository",
            SessionId = "turn-real-unauthorized",
            LlmControl = new LLMControlContextPayload { NyxIdAccessToken = "request-token-secret" },
        });
        await DeliverPublishedHistoryRequestsAsync(agent, publisher);

        var completed = (await eventStore.GetEventsAsync(actorId))
            .Where(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle().Which;
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Blocked);
        completed.AuthorizationRequired.ReasonCode.Should().Be("NYXID_UNAUTHORIZED");
        completed.AuthorizationRequired.ResourceUri.Should().Be("/repos/private");
        completed.ToolReceipts.Should().ContainSingle(receipt =>
            receipt.Status == AgentToolReceiptStatus.AuthorizationRequired);
        completed.ToolCalls.Should().ContainSingle(call =>
            call.CallId == "call-unauthorized" &&
            call.ToolName == "nyxid_proxy" &&
            call.ArgumentsJson == string.Empty);
        completed.ToString().Should()
            .NotContain("bearer-secret")
            .And.NotContain("query-secret")
            .And.NotContain("header-secret")
            .And.NotContain("request-token-secret")
            .And.NotContain("access_token");
        publisher.Published.OfType<ToolCallEvent>().Should().ContainSingle().Which.Should().Match<ToolCallEvent>(call =>
            call.CallId == "call-unauthorized" &&
            call.ToolName == "nyxid_proxy" &&
            call.ArgumentsJson == string.Empty);
        publisher.Published.OfType<ToolResultEvent>().Should().ContainSingle().Which.ToString()
            .Should().NotContain("bearer-secret").And.NotContain("query-secret").And.NotContain("header-secret");
        var frames = NyxIdChatCompletionAguiFrameBuilder.Build(
            new NyxIdChatSessionProjectionContext
            {
                RootActorId = actorId,
                SessionId = completed.SessionId,
                ProjectionKind = "nyxid-chat-session",
            },
            completed);
        frames.Any(frame => frame.Custom != null && frame.Custom.Name == "nyxid.authorization.required")
            .Should().BeTrue();
        frames.Any(frame => frame.RunFinished != null &&
                            frame.RunFinished.Status == Aevatar.AGUI.Contracts.RunCompletionStatus.Blocked)
            .Should().BeTrue();
        frames.Select(frame => frame.ToString()).Should()
            .NotContain(text => text.Contains("secret", StringComparison.OrdinalIgnoreCase));
        history.Saved.Should().ContainSingle();
        history.Saved.Single().Messages.Select(message => message.ToString()).Should()
            .NotContain(text => text.Contains("secret", StringComparison.OrdinalIgnoreCase));

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "ordinary follow-up",
            SessionId = "turn-after-unauthorized",
        });

        llmProviderFactory.StreamRequests.Should().HaveCount(2);
        var laterRequestMessages = llmProviderFactory.StreamRequests[1].Messages;
        var replayedAssistant = laterRequestMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        replayedAssistant.ToolCalls![0].Id.Should().Be("call-unauthorized");
        replayedAssistant.ToolCalls[0].Name.Should().Be("nyxid_proxy");
        replayedAssistant.ToolCalls[0].ArgumentsJson.Should()
            .NotContain("query-secret")
            .And.NotContain("header-secret");
        replayedAssistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        laterRequestMessages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-unauthorized");
        laterRequestMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments =>
                arguments.Contains("query-secret", StringComparison.Ordinal) ||
                arguments.Contains("header-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleChatRequest_NyxId403_ShouldRemainNormalTypedToolFailure()
    {
        const string actorId = "nyxid-chat-real-forbidden";
        var eventStore = new InMemoryEventStoreForTests();
        var history = new RecordingChatHistoryCommandPort();
        using var services = BuildServiceProvider(historyCommandPort: history, eventStore: eventStore);
        using var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(new FixedNyxIdResponseHandler(
                HttpStatusCode.Forbidden,
                """{"error":"forbidden","error_code":1002,"message":"approval timed out bearer-secret"}""")));
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-forbidden",
                        Name = "nyxid_proxy",
                        ArgumentsJson =
                            """{"service_id":"us-github-alpha","slug":"api-github","path":"/repos/private?access_token=query-secret","headers":{"X-Credential":"header-secret"}}""",
                    },
                }],
                [new LLMStreamChunk { DeltaContent = "The service request was denied." }],
            ]);
        var agent = CreateAgent(
            services,
            actorId,
            llmProviderFactory,
            [new StaticToolSource([new NyxIdProxyTool(client)])],
            loopbackHistoryDelivery: true);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "read private repository",
            SessionId = "turn-real-forbidden",
            LlmControl = new LLMControlContextPayload { NyxIdAccessToken = "request-token-secret" },
        });

        var completed = (await eventStore.GetEventsAsync(actorId))
            .Where(evt => evt.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should().ContainSingle().Which;
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
        completed.AuthorizationRequired.Should().BeNull();
        completed.ToolReceipts.Should().ContainSingle(receipt =>
            receipt.Status == AgentToolReceiptStatus.Error &&
            receipt.ErrorCode == "NYXID_PROXY_FORBIDDEN");
        completed.ToolCalls.Should().ContainSingle(call =>
            call.CallId == "call-forbidden" &&
            call.ToolName == "nyxid_proxy" &&
            call.ArgumentsJson == string.Empty);
        completed.ToString().Should()
            .NotContain("bearer-secret")
            .And.NotContain("query-secret")
            .And.NotContain("header-secret")
            .And.NotContain("request-token-secret");
        var frames = NyxIdChatCompletionAguiFrameBuilder.Build(
                new NyxIdChatSessionProjectionContext
                {
                    RootActorId = actorId,
                    SessionId = completed.SessionId,
                    ProjectionKind = "nyxid-chat-session",
                },
                completed);
        frames.Any(frame => frame.Custom != null && frame.Custom.Name == "nyxid.authorization.required")
            .Should().BeFalse();
        frames.Select(frame => frame.ToString()).Should()
            .NotContain(text => text.Contains("secret", StringComparison.OrdinalIgnoreCase));
        llmProviderFactory.StreamRequests.Should().HaveCount(2);
        var immediateFollowUpMessages = llmProviderFactory.StreamRequests[1].Messages;
        var failedAssistant = immediateFollowUpMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        failedAssistant.ToolCalls![0].Id.Should().Be("call-forbidden");
        failedAssistant.ToolCalls[0].Name.Should().Be("nyxid_proxy");
        failedAssistant.ToolCalls[0].ArgumentsJson.Should()
            .NotContain("query-secret")
            .And.NotContain("header-secret");
        failedAssistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        immediateFollowUpMessages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == "call-forbidden");
        immediateFollowUpMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments =>
                arguments.Contains("query-secret", StringComparison.Ordinal) ||
                arguments.Contains("header-secret", StringComparison.Ordinal));
        history.Saved.Should().ContainSingle();
        history.Saved.Single().Messages.Should().ContainSingle(message =>
            message.Role == "assistant" &&
            message.Status == "completed" &&
            message.Content == "The service request was denied.");
    }

    [Fact]
    public async Task HandleChatRequest_WhenProviderFails_ShouldArchiveOnlySafeFailureMessage()
    {
        var history = new RecordingChatHistoryCommandPort();
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var agent = CreateAgent(
            provider,
            "nyxid-chat-safe-failure-history",
            new ThrowingStreamingProviderFactory(
                new InvalidOperationException("provider failed with bearer-secret credential")),
            loopbackHistoryDelivery: true);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            ScopeId = "scope-a",
            Prompt = "hello",
            SessionId = "turn-failed",
        });

        var assistant = history.Saved.Should().ContainSingle().Which.Messages
            .Should().ContainSingle(static message => message.Role == "assistant").Which;
        assistant.Status.Should().Be("error");
        assistant.Content.Should().Be("The chat request failed. Please try again.");
        assistant.Error.Should().Be("The chat request failed. Please try again.");
        assistant.ToString().Should().NotContain("bearer-secret").And.NotContain("credential");
    }

    [Fact]
    public async Task HandleChatRequest_ShouldNotSaveHistoryWithoutScopeId()
    {
        var history = new RecordingChatHistoryCommandPort();
        using var provider = BuildServiceProvider(historyCommandPort: history);
        var llmProviderFactory = new StreamingToolLoopProviderFactory(
            [
                [
                    new LLMStreamChunk { DeltaContent = "direct answer" },
                ],
            ]);
        var agent = CreateAgent(provider, "nyxid-chat-no-scope", llmProviderFactory);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-no-scope",
        });

        history.Saved.Should().BeEmpty();
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
        var agent = CreateConversationAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = false,
        }));

        registry.UnregisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentKind,
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
        var agent = CreateConversationAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-a",
            CreatedLocally = true,
        }));

        registry.UnregisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentKind,
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
        var agent = CreateConversationAgent(provider, actorId);

        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationDeletionCompensationRequested
        {
            ScopeId = "scope-a",
            ActorId = actorId,
            Reason = "history_delete_failed",
        }));

        registry.RegisteredActors.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-a",
            NyxIdChatServiceDefaults.GAgentKind,
            actorId));
    }

    private static ServiceProvider BuildServiceProvider(
        IGAgentActorRegistryCommandPort? registryCommandPort = null,
        IActorRuntime? actorRuntime = null,
        IChatHistoryCommandPort? historyCommandPort = null,
        IEventStore? eventStore = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null)
    {
        eventStore ??= new InMemoryEventStoreForTests();
        callbackScheduler ??= new NoopRuntimeCallbackScheduler();
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<ISecretVault, InMemorySecretVault>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton(callbackScheduler)
            .AddSingleton<IAuditTrailAppender, AppendedAuditTrail>()
            .AddSingleton<IAuditActorIdentityHasher, StableIdentityHasher>()
            .AddSingleton<IAgentToolAdmissionLedger>(AlwaysStartingAgentToolAdmissionLedger.Instance)
            .AddSingleton<IAgentToolExecutionPort, AdmittedAgentToolExecutor>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));

        if (registryCommandPort is not null)
            services.AddSingleton(registryCommandPort);

        if (actorRuntime is not null)
            services.AddSingleton(actorRuntime);

        if (historyCommandPort is not null)
            services.AddSingleton(historyCommandPort);

        return services.BuildServiceProvider();
    }

    private static NyxIdChatGAgent CreateAgent(
        IServiceProvider provider,
        string actorId,
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        NyxIdRelayOptions? relayOptions = null,
        TimeProvider? timeProvider = null,
        AgentProfileTurnCatalogMaterializer? turnCatalogMaterializer = null,
        RoleChatExecutionOptions? chatExecutionOptions = null,
        bool loopbackHistoryDelivery = false)
    {
        var agent = new NyxIdChatGAgent(
            new SystemSkillOverlayPromptInjectionTests.StubBuiltInPromptFloorProvider(),
            provider.GetRequiredService<IAgentToolExecutionPort>(),
            llmProviderFactory: llmProviderFactory,
            toolSources: toolSources,
            relayOptions: relayOptions,
            timeProvider: timeProvider,
            turnCatalogMaterializer: turnCatalogMaterializer,
            chatExecutionOptions: chatExecutionOptions,
            chatToolRecoverySecretVault: provider.GetRequiredService<ISecretVault>())
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };

        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
        if (loopbackHistoryDelivery)
            agent.EventPublisher = new DirectChatHistoryLoopbackPublisher(agent);
        return agent;
    }

    private static NyxIdChatConversationGAgent CreateConversationAgent(
        IServiceProvider provider,
        string actorId,
        IActorDispatchPort? dispatchPort = null)
    {
        var agent = new NyxIdChatConversationGAgent(
            provider.GetService<IActorRuntime>() ?? new RecordingActorRuntime(),
            dispatchPort ?? new NoopActorDispatchPort(),
            TimeProvider.System)
        {
            Services = provider,
            EventSourcingBehaviorFactory = provider.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        var setId = typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setId.Invoke(agent, [actorId]);
        return agent;
    }

    private static async Task AssertBoundTurnMaterializationFailureRejectsAllToolsAsync(
        AgentProfileTurnCatalogMaterializer? turnCatalogMaterializer,
        string actorId)
    {
        var executeCount = 0;
        var tools = new IAgentTool[]
        {
            new DelegateTool("forged", _ =>
            {
                executeCount++;
                return "must not execute";
            }),
        };
        using var provider = BuildServiceProvider(historyCommandPort: new RecordingChatHistoryCommandPort());
        var llm = new StreamingToolLoopProviderFactory(
        [
            [new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "forged-call",
                    Name = "forged",
                    ArgumentsJson = "{}",
                },
            }],
            [new LLMStreamChunk { DeltaContent = "done" }],
        ]);
        await AppendCommittedEventsAsync(
            provider,
            actorId,
            new AgentProfileBoundEvent { Profile = BuildSealedProfile("profile-v1", "profile.route") });
        var agent = CreateAgent(
            provider,
            actorId,
            llm,
            [new StaticToolSource(tools)],
            turnCatalogMaterializer: turnCatalogMaterializer);

        await agent.ActivateAsync();
        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "run forged tool",
            SessionId = $"{actorId}-session",
        });

        llm.StreamRequests.Should().HaveCount(2);
        var firstRequest = llm.StreamRequests[0];
        firstRequest.Tools.Should().BeNull();
        firstRequest.ToolContext!.ToolVisibility.IsRestricted.Should().BeTrue();
        firstRequest.ToolContext.ToolVisibility.Allows("forged").Should().BeFalse();
        executeCount.Should().Be(0);
    }

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
        Propagation = new EnvelopePropagation { CorrelationId = Guid.NewGuid().ToString("N") },
    };

    private static async Task AppendCommittedEventsAsync(
        IServiceProvider provider,
        string actorId,
        params IMessage[] events)
    {
        var stateEvents = events.Select((evt, index) => new StateEvent
        {
            EventId = $"profile-binding-{index + 1}",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Version = index + 1,
            EventType = evt.Descriptor.FullName,
            EventData = Any.Pack(evt),
            AgentId = actorId,
        });

        await provider.GetRequiredService<IEventStore>()
            .AppendAsync(actorId, stateEvents, expectedVersion: 0);
    }

    private static StateEvent StateEventFor(string actorId, long version, IMessage evt) =>
        new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Version = version,
            EventType = evt.Descriptor.FullName,
            EventData = Any.Pack(evt),
            AgentId = actorId,
        };

    private static ChatRouteResolver NewChatRouteResolver() =>
        new(new StaticChatRouteFallbackProvider(string.Empty));

    private static AgentProfileSnapshot BuildSealedProfile(
        string profileVersion,
        string routeToolSetRef = "") =>
        AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = profileVersion,
            AgentKind = "nyxid.chat",
            RouteToolSetRef = routeToolSetRef,
        });

    private static AgentProfileSnapshot BuildSealedEnforcedProfile()
    {
        var member = new AgentProfileSkillMember
        {
            IntentId = "intent-alpha",
            RoutingDescription = "Route alpha requests.",
            SkillRef = new ExactRemoteSkillRef
            {
                Guid = ExactSkillGuid,
                LiteralVersion = ExactSkillVersion,
            },
            TaskToolPolicy = new AgentProfileToolPolicy(),
            SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
            ExpectedSkillName = ExactSkillName,
            ReviewedPublisherId = ExactSkillPublisher,
            SealedSkillSha256 = ExactSkillSha256,
        };
        member.ExplicitTriggerAliases.Add("/alpha");
        member.TaskToolPolicy.ToolNames.Add("task");

        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = "nyxid.chat",
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ClassifierTimeoutMs = 600,
            ExactSkillFetchTimeoutMs = 1_500,
            MaxSelectedSkillBytes = 256,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.MaximumToolPolicy.ToolNames.Add(["recovery", "task", "hidden"]);
        profile.RecoveryToolPolicy.ToolNames.Add("recovery");
        profile.Members.Add(member);
        return AgentProfileSnapshotCodec.Seal(profile);
    }

    private static AgentProfileSnapshot BuildSealedShadowProfile()
    {
        var profile = BuildSealedEnforcedProfile();
        profile.DeterministicPolicySha256 = ByteString.Empty;
        profile.ProfileVersion = "profile-shadow-v1";
        profile.PolicyRevision = "policy-shadow-v1";
        profile.ActivationMode = AgentProfileActivationMode.Shadow;
        return AgentProfileSnapshotCodec.Seal(profile);
    }

    private sealed class StaticChatRouteFallbackProvider(string modelName) : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() => new()
        {
            Action = new ChatRouteAction
            {
                ForwardToModel = new ForwardToModel { ModelName = modelName },
            },
            MatchedRuleId = string.Empty,
            UsedFallback = true,
            ResolvedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
    }

    private sealed class MissingForwardToModelFallbackProvider : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() => new()
        {
            Action = new ChatRouteAction(),
            MatchedRuleId = string.Empty,
            UsedFallback = true,
            ResolvedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
    }

    private sealed class StaticChatRoutePolicyQueryPort(ChatRoutePolicySnapshot? snapshot)
        : IChatRoutePolicyQueryPort
    {
        public static StaticChatRoutePolicyQueryPort ForSnapshot(ChatRoutePolicySnapshot? snapshot) => new(snapshot);

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class FixedAgentProfileResolver(NyxIdChatAgentProfileResolution resolution)
        : INyxIdChatAgentProfileResolver
    {
        public FixedAgentProfileResolver(AgentProfileSnapshot snapshot)
            : this(NyxIdChatAgentProfileResolution.Selected(
                snapshot,
                NyxIdChatAgentProfileSelectionSource.ScopeDefault))
        {
        }

        public AgentProfileSnapshot Snapshot => resolution.Profile!;
        public int ResolveCalls { get; private set; }
        public List<NyxIdChatAgentProfileSelectionRequest> Requests { get; } = [];

        public static FixedAgentProfileResolver Failure(NyxIdChatAgentProfileResolutionStatus status) =>
            new(NyxIdChatAgentProfileResolution.Failure(status));

        public Task<NyxIdChatAgentProfileResolution> ResolveAsync(
            NyxIdChatAgentProfileSelectionRequest request,
            CancellationToken ct = default)
        {
            ResolveCalls++;
            Requests.Add(request);
            return Task.FromResult(resolution);
        }

    }

    private sealed class RecordingGAgentActorRegistryCommandPort(List<string>? operations = null) : IGAgentActorRegistryCommandPort
    {
        public GAgentActorRegistryCommandStage RegisterStage { get; set; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;

        public List<GAgentActorRegistration> RegisteredActors { get; } = [];
        public List<GAgentActorRegistration> UnregisteredActors { get; } = [];

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations?.Add("registry.register");
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

    private sealed class RecordingChatHistoryCommandPort(List<string>? operations = null) : IChatHistoryCommandPort
    {
        public Exception? InitializeException { get; init; }
        public Exception? SaveException { get; set; }
        public List<ChatHistoryConversationInitialization> Initializations { get; } = [];
        public List<SavedChatHistory> Saved { get; } = [];
        public List<(string ScopeId, string ConversationId)> Deleted { get; } = [];

        public Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default)
        {
            Initializations.Add(request);
            return InitializeException is null
                ? Task.CompletedTask
                : Task.FromException(InitializeException);
        }

        public Task ReserveTurnDeliveryAsync(
            ChatHistoryTurnDeliveryReservation request,
            CancellationToken ct = default)
        {
            operations?.Add("history.reserve");
            return Task.CompletedTask;
        }

        public Task NotifyTurnTerminalAsync(
            ChatHistoryTurnTerminalNotification notification,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default)
        {
            if (SaveException is not null)
                return Task.FromException(SaveException);

            Saved.Add(new SavedChatHistory(scopeId, conversationId, meta, messages.ToArray()));
            return Task.CompletedTask;
        }

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(string scopeId, string conversationId, CancellationToken ct = default)
        {
            Deleted.Add((scopeId, conversationId));
            return Task.FromResult(ChatHistoryDeleteResult.Accepted());
        }
    }

    private sealed record SavedChatHistory(
        string ScopeId,
        string ConversationId,
        ConversationMeta Meta,
        IReadOnlyList<StoredChatMessage> Messages);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;

        public void Advance(TimeSpan amount) => _value = _value.Add(amount);
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

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingSelfDispatchPort(List<string>? operations = null) : IActorDispatchPort
    {
        public Exception? DispatchException { get; init; }
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations?.Add("dispatch");
            Calls.Add((actorId, envelope.Clone()));
            if (DispatchException is not null)
                throw DispatchException;
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActorRuntime(List<string>? operations = null) : IActorRuntime
    {
        public List<(System.Type Type, string? Id)> CreateCalls { get; } = [];
        public List<string> DestroyedActors { get; } = [];

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(new RecordingActor(id));

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            operations?.Add("runtime.create");
            CreateCalls.Add((typeof(TAgent), id));
            return Task.FromResult<IActor>(new RecordingActor(id ?? Guid.NewGuid().ToString("N")));
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreateCalls.Add((agentType, id));
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

    private sealed class NoopActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
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

    private sealed class ThrowingStreamingProviderFactory(Exception exception)
        : ILLMProviderFactory, ILLMProvider
    {
        public string Name => NyxIdChatServiceDefaults.ProviderName;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk();
            await Task.Yield();
            throw exception;
        }
    }

    private static async Task<JsonObject> ReadUntilFrameAsync(
        ChannelReader<JsonObject> reader,
        ICollection<JsonObject> observed,
        string expectedType,
        CancellationToken ct)
    {
        while (true)
        {
            var frame = await reader.ReadAsync(ct);
            observed.Add(frame);
            if (string.Equals(FrameType(frame), expectedType, StringComparison.Ordinal))
                return frame;
        }
    }

    private static string FrameType(JsonObject frame) =>
        frame["type"]?.GetValue<string>() ?? string.Empty;

    private sealed class ControlledProgressProviderFactory(bool emitTextToolCall)
        : ILLMProviderFactory, ILLMProvider
    {
        private readonly TaskCompletionSource _firstRoundRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _round;

        public TaskCompletionSource WaitingForFirstRoundRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FirstRoundReleased { get; private set; }
        public string Name => NyxIdChatServiceDefaults.ProviderName;
        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            if (_round++ == 0)
            {
                yield return new LLMStreamChunk { DeltaContent = "first chunk" };
                WaitingForFirstRoundRelease.TrySetResult();
                await _firstRoundRelease.Task.WaitAsync(ct);
                yield return emitTextToolCall
                    ? new LLMStreamChunk
                    {
                        DeltaContent = """
                            <function_calls>
                            <invoke name="controlled_lookup">
                            <parameter name="input">controlled</parameter>
                            </invoke>
                            </function_calls>
                            """,
                    }
                    : new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "controlled-call-1",
                        Name = "controlled_lookup",
                        ArgumentsJson = "{}",
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk
                {
                    DeltaContent = "final answer",
                    Usage = new TokenUsage(3, 2, 5),
                };
            }

            yield return new LLMStreamChunk { IsLast = true };
        }

        public void ReleaseFirstRound()
        {
            FirstRoundReleased = true;
            _firstRoundRelease.TrySetResult();
        }
    }

    private sealed class ControlledProgressTool : IAgentTool
    {
        private readonly TaskCompletionSource<string> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Released { get; private set; }
        private string _displayName = "Controlled lookup";
        public string Name => "controlled_lookup";
        public string Description => "Looks up controlled test data.";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => true;
        public Aevatar.Foundation.Abstractions.Tools.ToolPresentationDescriptor Presentation =>
            ToolPresentationDescriptors.BuiltIn(Name, _displayName, Description);

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            _displayName = "Renamed after invocation start";
            Started.TrySetResult();
            return await _release.Task.WaitAsync(ct);
        }

        public void Release(string result)
        {
            Released = true;
            _release.TrySetResult(result);
        }
    }

    private sealed class FlushedSseFrameStream : MemoryStream
    {
        private readonly Channel<JsonObject> _frames =
            Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
        private readonly Queue<JsonObject> _pending = [];

        public ChannelReader<JsonObject> Frames => _frames.Reader;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var write = base.WriteAsync(buffer, cancellationToken);
            var raw = Encoding.UTF8.GetString(buffer.Span).Trim();
            if (raw.StartsWith("data: ", StringComparison.Ordinal) &&
                JsonNode.Parse(raw[6..]) is JsonObject frame)
            {
                _pending.Enqueue(frame);
            }

            return write;
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await base.FlushAsync(cancellationToken);
            while (_pending.TryDequeue(out var frame))
                await _frames.Writer.WriteAsync(frame, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _frames.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }

    private sealed class FixedNyxIdResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class CountingToolSetRegistry : IToolSetRegistry
    {
        public int ResolveCount { get; private set; }

        public IReadOnlyList<string> GetRegisteredNames() => [];

        public ToolSetResolveResult Resolve(string? name)
        {
            ResolveCount++;
            name ??= string.Empty;
            return ToolSetResolveResult.Failure(new ToolSetResolveError(
                ToolSetResolveError.UnknownNameCode,
                name,
                "missing",
                []));
        }
    }

    private sealed class StaticProfileToolSetRegistry(
        string name,
        IReadOnlyList<IAgentTool> tools) : IToolSetRegistry
    {
        public int ResolveCount { get; private set; }

        public IReadOnlyList<string> GetRegisteredNames() => [name];

        public ToolSetResolveResult Resolve(string? requestedName)
        {
            ResolveCount++;
            return string.Equals(requestedName, name, StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(name, [new StaticToolSource(tools)])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    requestedName ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
        }
    }

    private sealed class BlockingProfileToolSetRegistry(
        string name,
        IAgentToolSource source) : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => [name];

        public ToolSetResolveResult Resolve(string? requestedName) =>
            string.Equals(requestedName, name, StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(name, [source])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    requestedName ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
    }

    private sealed class ReleasableBlockingToolSource : IAgentToolSource
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public bool CancellationObserved { get; private set; }

        public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _started.TrySetResult();
            try
            {
                await _released.Task.WaitAsync(ct);
                return [];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public void Release() => _released.TrySetResult();
    }

    private sealed class ThrowingNameToolSetRegistry : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => ["profile.route"];

        public ToolSetResolveResult Resolve(string? name) =>
            ToolSetResolveResult.Success(
                name ?? string.Empty,
                [new StaticToolSource([new ThrowingNameTool()])]);
    }

    private sealed class ThrowingNameTool : IAgentTool
    {
        public string Name => throw new InvalidOperationException("tool name unavailable");
        public string Description => "unreachable";
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class NoMatchClassifier : IAgentProfileTurnClassifier
    {
        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(AgentProfileTurnClassificationResult.NoMatch());
    }

    private sealed class RecordingExactFetcher(ExactRemoteSkillFetchResult result) : IExactRemoteSkillFetcher
    {
        public int CallCount { get; private set; }
        public string? AccessToken { get; private set; }
        public ExactRemoteSkillRef? SkillRef { get; private set; }

        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            CallCount++;
            AccessToken = accessToken;
            SkillRef = skillRef.Clone();
            return Task.FromResult(result);
        }
    }

    private sealed class CancellationBlockingExactFetcher : IExactRemoteSkillFetcher
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public bool CancellationObserved { get; private set; }

        public async Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            _started.TrySetResult();
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                canceled);
            try
            {
                await canceled.Task;
                throw new InvalidOperationException("The exact fetch should have been canceled.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class DelegateTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => $"{name} test tool";
        public string ParametersSchema => """{"type":"object"}""";
        public AgentToolReceipt? CreateSuccessReceipt(
            string callId,
            string toolName,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(execute(argumentsJson));
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private sealed class VerifiedMissingServiceTool : IAgentTool
    {
        public string Name => "nyxid_require_service";
        public string Description => "Verified missing service test fixture";
        public string ParametersSchema => """{"type":"object"}""";
        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(
                """{"blocked":true,"service_slug":"api-github","reason_code":"USER_SERVICE_NOT_VISIBLE","safe_message":"No caller-visible NyxID UserService matches the requested service."}""");

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
                ErrorCode = "USER_SERVICE_NOT_VISIBLE",
                ErrorMessage = "No caller-visible NyxID UserService matches the requested service.",
                AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                {
                    ServiceSlug = "api-github",
                    ResourceUri = "/repos/private",
                    ReasonCode = "USER_SERVICE_NOT_VISIBLE",
                    SafeMessage = "No caller-visible NyxID UserService matches the requested service.",
                },
            };
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public bool FailHistoryDeliveryRequests { get; init; }
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
            if (FailHistoryDeliveryRequests && evt is NyxIdDirectChatHistoryDeliveryRequested)
                return Task.FromException(new InvalidOperationException("simulated history request publish failure"));
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

    private sealed class DirectChatHistoryLoopbackPublisher(NyxIdChatGAgent agent) : IEventPublisher
    {
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            direction == TopologyAudience.Self && evt is NyxIdDirectChatHistoryDeliveryRequested request
                ? agent.HandleDirectChatHistoryDeliveryRequestedAsync(request)
                : Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null) => Task.CompletedTask;
    }
}
