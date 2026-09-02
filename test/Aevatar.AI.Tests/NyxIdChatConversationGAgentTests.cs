using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.AGUI.Contracts;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Type = System.Type;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatConversationGAgentTests
{
    [Fact]
    public void ConversationCreateContract_ShouldCarryTypedFirstTurn()
    {
        var field = NyxIdChatConversationCreateCommand.Descriptor
            .FindFieldByName("first_turn");

        field.Should().NotBeNull();
        field!.MessageType.FullName.Should().Be(
            NyxIdChatStartTurnCommand.Descriptor.FullName);
    }

    [Fact]
    public async Task CreateConversation_WithFirstTurn_ShouldCommitOwnerOnceAndRecoverIt()
    {
        const string actorId = "conversation-owner-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var first = CreateController(services, actorId, dispatch);
        await first.ActivateAsync();
        var firstTurn = CreateStartTurnCommand();
        firstTurn.ConversationActorId = actorId;
        SetOwner(firstTurn, " owner-alpha ");
        var create = new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-alpha",
            CreatedLocally = true,
            RequestedActorId = actorId,
            FirstTurn = firstTurn,
        };

        await first.HandleEventAsync(CreateEnvelope(actorId, create));

        var committed = await eventStore.GetEventsAsync(actorId);
        committed.First(static item =>
                item.EventData.Is(NyxIdChatConversationCreationStartedEvent.Descriptor))
            .EventData.Unpack<NyxIdChatConversationCreationStartedEvent>()
            .OwnerSubject.Should().Be("owner-alpha");
        first.State.OwnerSubject.Should().Be("owner-alpha");
        var countAfterFirst = committed.Count;
        var dispatchCountAfterFirst = dispatch.OperationCalls.Count;

        await first.HandleEventAsync(CreateEnvelope(actorId, create.Clone()));

        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(countAfterFirst);
        dispatch.OperationCalls.Should().HaveCount(dispatchCountAfterFirst);

        var recovered = CreateController(services, actorId);
        await recovered.ActivateAsync();
        recovered.State.OwnerSubject.Should().Be("owner-alpha");
    }

    [Fact]
    public async Task StartTurn_WithConflictingOwner_ShouldCommitSafeRejectionWithoutDispatch()
    {
        const string actorId = "conversation-owner-conflict";
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId, dispatch);
        await agent.ActivateAsync();
        var firstTurn = CreateStartTurnCommand();
        firstTurn.ConversationActorId = actorId;
        SetOwner(firstTurn, "owner-alpha");
        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-alpha",
            CreatedLocally = true,
            RequestedActorId = actorId,
            FirstTurn = firstTurn,
        }));
        var dispatchCount = dispatch.OperationCalls.Count;
        var conflicting = firstTurn.Clone();
        conflicting.TurnId = "turn-beta";
        conflicting.TaskId = "task-beta";
        conflicting.ClientRequestId = "client-beta";
        conflicting.CommandId = "command-beta";
        SetOwner(conflicting, "owner-beta");

        await agent.HandleEventAsync(CreateEnvelope(actorId, conflicting));

        var rejection = (await eventStore.GetEventsAsync(actorId))[^1].EventData
            .Unpack<NyxIdChatTurnAdmissionRejectedEvent>();
        rejection.ReasonCode.Should().Be("NYXID_CHAT_OWNER_MISMATCH");
        rejection.ToString().Should().NotContain("owner-alpha").And.NotContain("owner-beta");
        agent.State.OwnerSubject.Should().Be("owner-alpha");
        dispatch.OperationCalls.Should().HaveCount(dispatchCount);
    }

    [Fact]
    public async Task StartTurn_OnOwnerlessConversation_ShouldRejectOwnerClaim()
    {
        const string actorId = "conversation-ownerless";
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId, dispatch);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(actorId, new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-alpha",
            CreatedLocally = true,
            RequestedActorId = actorId,
        }));
        var turn = CreateStartTurnCommand();
        turn.ConversationActorId = actorId;
        SetOwner(turn, "owner-alpha");

        await agent.HandleEventAsync(CreateEnvelope(actorId, turn));

        var rejection = (await eventStore.GetEventsAsync(actorId))[^1].EventData
            .Unpack<NyxIdChatTurnAdmissionRejectedEvent>();
        rejection.ReasonCode.Should().Be("NYXID_CHAT_OWNER_MISMATCH");
        agent.State.OwnerSubject.Should().BeEmpty();
        dispatch.OperationCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateConversation_WithOwnerlessFirstTurn_ShouldRejectBeforeCommit()
    {
        const string actorId = "conversation-owner-required";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId);
        await agent.ActivateAsync();
        var firstTurn = CreateStartTurnCommand();
        firstTurn.ConversationActorId = actorId;

        var act = () => agent.HandleEventAsync(CreateEnvelope(actorId,
            new NyxIdChatConversationCreateCommand
            {
                ScopeId = "scope-alpha",
                CreatedLocally = true,
                RequestedActorId = actorId,
                FirstTurn = firstTurn,
            }));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*owner_subject is required*");
        (await eventStore.GetEventsAsync(actorId)).Should().BeEmpty();
    }

    [Fact]
    public void ActionRequestedAguiFrames_ShouldOmitConversationOwner()
    {
        var evt = new NyxIdChatActionRequestedEvent
        {
            Request = new NyxIdChatActionRequestState
            {
                ActionRequestId = "action-alpha",
                OriginTurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                Action = NyxIdAssistantActionKind.ServiceConnect,
            },
            State = new NyxIdChatConversationGAgentState
            {
                ConversationActorId = "conversation-alpha",
                ScopeId = "scope-alpha",
                OwnerSubject = "owner-alpha",
            },
        };

        var frames = NyxIdChatConversationAguiFrameBuilder.BuildActionRequested(
            "conversation-alpha",
            "turn-alpha",
            evt,
            1);

        string.Join('\n', frames.Select(static frame =>
                Encoding.UTF8.GetString(frame.ToByteArray())))
            .Should().NotContain("owner-alpha");
        NyxIdAssistantActionRequestWirePayload.Descriptor.Fields.InFieldNumberOrder()
            .Should().NotContain(field => field.Name == "owner_subject");
    }

    [Fact]
    public void PublicIdentity_ShouldBeStableAndScopeBound()
    {
        var first = NyxIdChatPublicIdentity.CreateConversationActorId(
            "scope-alpha",
            "client-alpha");

        first.Should().Be(NyxIdChatPublicIdentity.CreateConversationActorId(
            "scope-alpha",
            "client-alpha"));
        first.Should().NotBe(NyxIdChatPublicIdentity.CreateConversationActorId(
            "scope-beta",
            "client-alpha"));
        NyxIdChatPublicIdentity.CreateTurnId(first, "client-alpha")
            .Should().StartWith("turn-");
    }

    [Fact]
    public void PublicAndLegacyActors_ShouldHaveDistinctExplicitKinds()
    {
        typeof(NyxIdChatConversationGAgent).GetCustomAttribute<GAgentAttribute>()!.Kind
            .Should().Be(NyxIdChatServiceDefaults.GAgentKind);
        typeof(NyxIdChatGAgent).GetCustomAttribute<GAgentAttribute>()!.Kind
            .Should().Be(NyxIdChatServiceDefaults.LegacyGAgentKind);
    }

    [Fact]
    public void LegacyActor_ShouldNotOwnPublicConversationLifecycleHandlers()
    {
        var lifecyclePayloadTypes = new HashSet<Type>
        {
            typeof(NyxIdChatConversationCreateCommand),
            typeof(NyxIdChatConversationCreationCompensationRequested),
            typeof(NyxIdChatConversationDeleteCommand),
            typeof(NyxIdChatConversationDeletionCompensationRequested),
        };

        var subscribedLifecyclePayloads = typeof(NyxIdChatGAgent)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static method => method.GetCustomAttribute<EventHandlerAttribute>() is not null)
            .SelectMany(static method => method.GetParameters().Take(1))
            .Select(static parameter => parameter.ParameterType)
            .Where(lifecyclePayloadTypes.Contains)
            .ToArray();

        subscribedLifecyclePayloads.Should().BeEmpty(
            "the public conversation controller must be the only lifecycle authority");
    }

    [Fact]
    public void ResponsiveActorTypes_ShouldBeAvailable()
    {
        var assembly = typeof(NyxIdChatStartTurnCommand).Assembly;

        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatConversationGAgent")
            .Should().NotBeNull();
        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatTurnGAgent")
            .Should().NotBeNull();
        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatTurnOperationExecutor")
            .Should().NotBeNull();
        assembly.GetType("Aevatar.GAgents.NyxidChat.NyxIdChatTurnActorIds")
            .Should().NotBeNull();
    }

    [Fact]
    public void ResponsiveActors_ShouldDependOnNarrowRuntimeNeutralPorts()
    {
        var assembly = typeof(NyxIdChatStartTurnCommand).Assembly;
        var executorPort = assembly.GetType(
            "Aevatar.GAgents.NyxidChat.INyxIdChatTurnOperationExecutor");

        executorPort.Should().NotBeNull();
        typeof(NyxIdChatConversationGAgent).GetConstructor(
        [
            typeof(IActorRuntime),
            typeof(IActorDispatchPort),
            typeof(TimeProvider),
        ]).Should().NotBeNull();
        typeof(NyxIdChatTurnGAgent).GetConstructor(
        [
            executorPort!,
            typeof(IActorDispatchPort),
            typeof(TimeProvider),
        ]).Should().NotBeNull();
    }

    [Fact]
    public void TurnActorAddress_ShouldBeStableOpaqueAndTurnScoped()
    {
        var method = typeof(NyxIdChatTurnActorIds).GetMethod(
            "ForTurn",
            [typeof(string), typeof(string)]);

        method.Should().NotBeNull();
        var first = (string)method!.Invoke(null, ["conversation-alpha", "turn-alpha"])!;
        var replay = (string)method.Invoke(null, ["conversation-alpha", "turn-alpha"])!;
        var otherTurn = (string)method.Invoke(null, ["conversation-alpha", "turn-beta"])!;

        first.Should().Be(replay);
        first.Should().StartWith("nyxid-chat-turn:");
        first.Should().NotContain("conversation-alpha").And.NotContain("turn-alpha");
        otherTurn.Should().NotBe(first);
    }

    [Fact]
    public async Task StartTurn_ShouldCommitRequestedWaterlineBeforeCreatingAndDispatchingTurnActor()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations);
        var eventStore = new InMemoryEventStoreForTests();
        NyxIdChatConversationGAgent? agent = null;
        NyxIdChatConversationGAgentState? stateObservedAtDispatch = null;
        IReadOnlyList<StateEvent>? eventsObservedAtDispatch = null;
        IReadOnlyList<StateEvent>? eventsObservedAtReservation = null;
        var history = new RecordingChatHistoryCommandPort(
            operations,
            async _ =>
            {
                eventsObservedAtReservation = await eventStore.GetEventsAsync(conversationActorId);
            });
        var dispatchPort = new RecordingActorDispatchPort(
            operations,
            async (actorId, envelope) =>
            {
                actorId.Should().Be(NyxIdChatTurnActorIds.ForTurn(conversationActorId, "turn-alpha"));
                stateObservedAtDispatch = agent!.State.Clone();
                eventsObservedAtDispatch = await eventStore.GetEventsAsync(conversationActorId);
                envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor).Should().BeTrue();
            });
        using var services = BuildEventSourcingServices(eventStore, history);
        agent = new NyxIdChatConversationGAgent(
            runtime,
            dispatchPort,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();

        var command = CreateStartTurnCommand();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));

        operations.Should().Equal("history.reserve", "create", "link", "dispatch");
        var reservation = history.Reservations.Should().ContainSingle().Which;
        reservation.DeliveryId.Should().NotBeNullOrWhiteSpace();
        reservation.ScopeId.Should().Be("scope-alpha");
        reservation.ConversationId.Should().Be(conversationActorId);
        reservation.TurnId.Should().Be("turn-alpha");
        reservation.UserText.Should().Be("hello");
        reservation.SourceActorId.Should().Be(conversationActorId);
        reservation.SourceCommandId.Should().Be("command-alpha");
        reservation.SourceCorrelationId.Should().Be("correlation-alpha");
        reservation.RequestFingerprint.Should().NotBeNullOrWhiteSpace();
        reservation.CreateConversationIfMissing.Should().BeTrue();
        reservation.ExposeCreateRecovery.Should().BeFalse();
        eventsObservedAtReservation.Should().ContainSingle().Which.EventData
            .Is(NyxIdChatTurnStartedEvent.Descriptor).Should().BeTrue();
        runtime.CreateCalls.Should().ContainSingle().Which.Should().Be(
            (typeof(NyxIdChatTurnGAgent), NyxIdChatTurnActorIds.ForTurn(conversationActorId, "turn-alpha")));
        runtime.LinkCalls.Should().ContainSingle().Which.Should().Be(
            (conversationActorId, NyxIdChatTurnActorIds.ForTurn(conversationActorId, "turn-alpha")));
        dispatchPort.Calls.Should().ContainSingle();

        stateObservedAtDispatch.Should().NotBeNull();
        stateObservedAtDispatch!.ConversationActorId.Should().Be(conversationActorId);
        stateObservedAtDispatch.ScopeId.Should().Be("scope-alpha");
        stateObservedAtDispatch.ActiveTurn.TurnId.Should().Be("turn-alpha");
        stateObservedAtDispatch.ActiveTurn.TaskId.Should().Be("task-alpha");
        stateObservedAtDispatch.ActiveTurn.CommandId.Should().Be("command-alpha");
        stateObservedAtDispatch.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        stateObservedAtDispatch.ActiveTask.TaskId.Should().Be("task-alpha");
        stateObservedAtDispatch.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        var requestedStep = stateObservedAtDispatch.ActiveTask.Steps.Should().ContainSingle().Which;
        requestedStep.Kind.Should().Be(NyxIdChatStepKind.Llm);
        requestedStep.Status.Should().Be(NyxIdChatStepStatus.Running);
        requestedStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        requestedStep.Operation.Key.ConversationActorId.Should().Be(conversationActorId);
        requestedStep.Operation.Key.TurnId.Should().Be("turn-alpha");
        requestedStep.Operation.Key.TaskId.Should().Be("task-alpha");
        requestedStep.Operation.Key.OperationGeneration.Should().Be(1);

        eventsObservedAtDispatch.Should().HaveCount(2);
        eventsObservedAtDispatch!.Select(static item => item.EventData.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnStartedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatHistoryDeliveryReservationDispatchedEvent()).TypeUrl);
        var committedEvents = await eventStore.GetEventsAsync(conversationActorId);
        committedEvents.Select(static item => item.EventData.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnStartedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatHistoryDeliveryReservationDispatchedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatOperationDispatchedEvent()).TypeUrl);
        agent.State.HistoryDeliveryReservation.Should().NotBeNull();
        agent.State.HistoryDeliveryReservation.DeliveryId.Should().Be(reservation.DeliveryId);
        agent.State.HistoryDeliveryReservation.Dispatched.Should().BeTrue();
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should()
            .Be(NyxIdChatOperationPhase.Dispatched);
    }

    [Fact]
    public async Task StartTurn_ExactReplay_ShouldNotMutateOrRepeatSideEffects()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations);
        var history = new RecordingChatHistoryCommandPort(operations);
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            runtime,
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        var command = CreateStartTurnCommand();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));
        var stateAfterFirst = agent.State.ToByteArray();
        var operationsAfterFirst = operations.ToArray();
        var eventCountAfterFirst = (await eventStore.GetEventsAsync(conversationActorId)).Count;
        var reservationCountAfterFirst = history.Reservations.Count;
        var createCountAfterFirst = runtime.CreateCalls.Count;
        var linkCountAfterFirst = runtime.LinkCalls.Count;
        var dispatchCountAfterFirst = dispatch.Calls.Count;

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command.Clone()));

        (await eventStore.GetEventsAsync(conversationActorId)).Should()
            .HaveCount(eventCountAfterFirst);
        agent.State.ToByteArray().Should().Equal(stateAfterFirst);
        operations.Should().Equal(operationsAfterFirst);
        history.Reservations.Should().HaveCount(reservationCountAfterFirst);
        runtime.CreateCalls.Should().HaveCount(createCountAfterFirst);
        runtime.LinkCalls.Should().HaveCount(linkCountAfterFirst);
        dispatch.Calls.Should().HaveCount(dispatchCountAfterFirst);
    }

    [Fact]
    public async Task StartTurn_WithBoundProfile_ShouldCommitAndDispatchTurnAuthority()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(
            eventStore,
            registryCommandPort: new RecordingGAgentActorRegistryCommandPort());
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_require_service" },
            },
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatConversationCreateCommand
            {
                ScopeId = "scope-alpha",
                CreatedLocally = true,
                RequestedActorId = conversationActorId,
                AgentProfile = profile,
                FirstTurn = WithOwner(CreateStartTurnCommand(), "owner-alpha"),
            }));

        agent.State.ActiveTurn.AgentProfileTurnAuthority.Should().NotBeNull();
        agent.State.ActiveTurn.AgentProfileTurnAuthority.ReconciliationKey.SessionId
            .Should().Be("turn-alpha");
        var command = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        command.Llm.AgentProfile.Should().BeEquivalentTo(profile);
        command.Llm.AgentProfileTurnAuthority.Should().BeEquivalentTo(
            agent.State.ActiveTurn.AgentProfileTurnAuthority);
    }

    [Fact]
    public async Task StartTurn_WithShadowProfile_ShouldDispatchLegacyUnprofiledPair()
    {
        const string conversationActorId = "conversation-shadow";
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-shadow",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_require_service" },
            },
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ActivationMode = AgentProfileActivationMode.Shadow,
        });
        var start = CreateStartTurnCommand();
        start.ConversationActorId = conversationActorId;
        SetOwner(start, "owner-alpha");

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatConversationCreateCommand
            {
                ScopeId = "scope-alpha",
                CreatedLocally = true,
                RequestedActorId = conversationActorId,
                AgentProfile = profile,
                FirstTurn = start,
            }));

        agent.State.AgentProfile.Should().BeEquivalentTo(profile);
        agent.State.ActiveTurn.AgentProfileTurnAuthority.Should().BeNull();
        var command = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        command.Llm.AgentProfile.Should().BeNull();
        command.Llm.AgentProfileTurnAuthority.Should().BeNull();
    }

    [Fact]
    public async Task NaturalProfiledServiceConnect_ShouldCommitOneRichCardAndBlockedTerminal()
    {
        const string conversationActorId = "conversation-profiled-connect";
        const string selectedSkillPrompt =
            "Call nyxid_require_service for the requested catalog service.";
        var skillHash = ByteString.CopyFrom(
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());
        var requireService = new VerifiedServiceConnectTool();
        IAgentTool[] routeTools =
        [
            new CanonicalProfileTool("nyxid_service_inventory"),
            new CanonicalProfileTool("nyxid_catalog"),
            requireService,
        ];
        var classifierProvider = new NaturalServiceConnectClassifierProvider();
        var classifier = new StreamingAgentProfileTurnClassifier(
            new FixedLlmProviderFactory(classifierProvider));
        var materializer = new AgentProfileTurnCatalogMaterializer(
            new FixedToolSetRegistry("profile.route", new FixedToolSource(routeTools)),
            classifier,
            new FixedSkillFetcher(
                "skill-service-connect",
                "1.0",
                "service-connect",
                "reviewed-publisher",
                skillHash,
                $"---\nname: service-connect\n---\n{selectedSkillPrompt}"));
        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-nyxid-chat",
            ProfileVersion = "profile-v2",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v2",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ClassifierTimeoutMs = 1_000,
            ExactSkillFetchTimeoutMs = 1_000,
            MaxSelectedSkillBytes = 1_024,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.MaximumToolPolicy.ToolNames.Add(routeTools.Select(static tool => tool.Name));
        profile.RecoveryToolPolicy.ToolNames.Add(["nyxid_service_inventory", "nyxid_catalog"]);
        profile.Members.Add(new AgentProfileSkillMember
        {
            IntentId = "service_connect",
            RoutingDescription = "Connect a requested NyxID catalog service.",
            SkillRef = new ExactRemoteSkillRef
            {
                Guid = "skill-service-connect",
                LiteralVersion = "1.0",
            },
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_require_service" },
            },
            SideEffectClass = AgentProfileSideEffectClass.ExternalHandoff,
            ExpectedSkillName = "service-connect",
            ReviewedPublisherId = "reviewed-publisher",
            SealedSkillSha256 = skillHash,
        });
        profile = AgentProfileSnapshotCodec.Seal(profile);

        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(
            eventStore,
            registryCommandPort: new RecordingGAgentActorRegistryCommandPort());
        var agent = CreateController(
            services,
            conversationActorId,
            dispatch,
            materializer);
        await agent.ActivateAsync();
        var start = CreateStartTurnCommand();
        start.ConversationActorId = conversationActorId;
        start.Prompt = "我要连接 AWS Cost Explorer";
        start.ToolContext.Credentials.NyxIdCredentialKind =
            AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer;
        SetOwner(start, "owner-alpha");
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatConversationCreateCommand
            {
                ScopeId = start.ScopeId,
                CreatedLocally = true,
                RequestedActorId = conversationActorId,
                AgentProfile = profile,
                FirstTurn = start,
            }));

        var provider = new ServiceConnectToolCallProvider();
        var generationExecutor = new AgentRunReplyGenerationExecutor(
            new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask),
            new NyxIdConversationReplyGenerator(
                provider,
                new BuiltInPromptFloorProvider(),
                toolExecutionPort: services.GetRequiredService<IAgentToolExecutionPort>()),
            interactiveReplyCollector: null,
            relayOptions: null,
            logger: NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var turnExecutor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            materializer);
        var session = new NyxIdChatTransientExecutionSession();
        var firstCommand = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        var llmExecution = await turnExecutor.ExecuteAsync(
            firstCommand,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        AgentToolExecutionContextMapper.FromPayload(session.StepState!.OwnerFallbackToolContext)
            .Credentials.NyxIdCredentialKind.Should()
            .Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, llmExecution.Result));
        dispatch.OperationCalls.Should().HaveCount(2);
        var toolCommand = dispatch.OperationCalls[1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        var toolExecution = await turnExecutor.ExecuteAsync(
            toolCommand,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, toolExecution.Result));

        var classifierRequest = classifierProvider.Requests.Should().ContainSingle().Which;
        classifierRequest.Messages.Single(static message => message.Role == "system").Content.Should()
            .Contain("final requested outcome")
            .And.Contain("external_handoff")
            .And.Contain("read_only");
        var classificationInput = classifierRequest.Messages
            .Single(static message => message.Role == "user").Content;
        using var classificationDocument = JsonDocument.Parse(classificationInput!);
        classificationDocument.RootElement.GetProperty("user_message").GetString()
            .Should().Be("我要连接 AWS Cost Explorer");
        classificationDocument.RootElement.GetProperty("intents").GetArrayLength().Should().Be(1);
        classificationDocument.RootElement.GetProperty("intents")[0]
            .GetProperty("side_effect_class").GetString().Should().Be("external_handoff");
        var llmRequest = provider.Requests.Should().ContainSingle().Which;
        llmRequest.Tools.Should().HaveCount(routeTools.Length);
        foreach (var tool in routeTools)
            llmRequest.Tools.Should().Contain(candidate => ReferenceEquals(candidate, tool));
        llmRequest.Messages.Single(static message => message.Role == "system").Content.Should()
            .Contain("Selected intent: service_connect")
            .And.Contain(selectedSkillPrompt);
        requireService.ExecutionCount.Should().Be(1);
        requireService.SourceReadableBearerToken.Should().Be("runtime-token-alpha");

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var action = committed
            .Where(static stateEvent =>
                stateEvent.EventData.Is(NyxIdChatActionRequestedEvent.Descriptor))
            .Should().ContainSingle().Which.EventData.Unpack<NyxIdChatActionRequestedEvent>();
        action.Request.SchemaVersion.Should().Be(4);
        action.Request.Action.Should().Be(NyxIdAssistantActionKind.ServiceConnect);
        action.Request.Params.CatalogServiceConnect.ServiceSlug.Should().Be("aws-cost-explorer");
        action.OriginTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        action.OriginTurn.FailureCode.Should().Be(NyxIdChatBrowserActions.ActionRequested);
        action.Task.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
        action.Task.FailureCode.Should().Be(NyxIdChatBrowserActions.ActionRequested);
        action.State.PendingActions.Should().ContainSingle().Which
            .Should().BeEquivalentTo(action.Request);
        agent.State.Should().BeEquivalentTo(action.State);

        var frames = NyxIdChatConversationAguiFrameBuilder.BuildActionRequested(
            conversationActorId,
            start.TurnId,
            action,
            action.State.ProgressSequence);
        var customFrames = frames.Where(static frame => frame.Custom is not null).ToArray();
        var richCard = customFrames.Should().ContainSingle(frame =>
            frame.Custom.Name == NyxIdChatConversationAguiFrameBuilder.ActionRequestEventName).Which;
        var wirePayload = richCard.Custom.Payload
            .Unpack<NyxIdAssistantActionRequestWirePayload>();
        wirePayload.SchemaVersion.Should().Be(4);
        wirePayload.Action.Should().Be("service.connect");
        wirePayload.Params.CatalogService.ServiceSlug.Should().Be("aws-cost-explorer");
        var finishedFrames = frames.Where(static frame => frame.RunFinished is not null).ToArray();
        finishedFrames.Should().ContainSingle().Which.RunFinished.Status
            .Should().Be(RunCompletionStatus.Blocked);
    }

    [Fact]
    public async Task StartTurn_InputPartsOnly_ShouldReserveSafeTranscriptTextAndDispatchOperation()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations);
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        var command = CreateStartTurnCommand();
        command.Prompt = string.Empty;
        command.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Image,
            Text = "raw-part-text-sentinel",
            DataBase64 = "raw-part-base64-sentinel",
            MediaType = "image/private-sentinel",
            Uri = "https://private.invalid/raw-part-uri-sentinel",
            Name = "raw-part-name-sentinel",
        });

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));

        operations.Should().Equal("history.reserve", "create", "link", "dispatch");
        var reservation = history.Reservations.Should().ContainSingle().Which;
        reservation.UserText.Should().Be("Shared input content.");
        reservation.ToString().Should()
            .NotContain("raw-part-text-sentinel")
            .And.NotContain("raw-part-base64-sentinel")
            .And.NotContain("raw-part-uri-sentinel")
            .And.NotContain("raw-part-name-sentinel");
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);
    }

    [Fact]
    public async Task StartTurn_ReusedIdentityWithDifferentInputParts_ShouldRejectInsteadOfExactReplay()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations);
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        var command = CreateStartTurnCommand();
        command.Prompt = string.Empty;
        command.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Text,
            Text = "first input",
        });
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));
        var beforeReplay = await eventStore.GetEventsAsync(conversationActorId);

        var conflicting = command.Clone();
        conflicting.InputParts.Clear();
        conflicting.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Text,
            Text = "different input",
        });
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, conflicting));

        var afterReplay = await eventStore.GetEventsAsync(conversationActorId);
        afterReplay.Should().HaveCount(beforeReplay.Count + 1);
        afterReplay[^1].EventData.Is(NyxIdChatTurnAdmissionRejectedEvent.Descriptor)
            .Should().BeTrue();
        history.Reservations.Should().ContainSingle();
        operations.Should().Equal("history.reserve", "create", "link", "dispatch");
    }

    [Fact]
    public async Task StartTurn_ReusedIdentityWithDifferentCommandId_ShouldRejectInsteadOfExactReplay()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        var command = CreateStartTurnCommand();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));
        var beforeConflict = await eventStore.GetEventsAsync(conversationActorId);

        var conflicting = command.Clone();
        conflicting.CommandId = "different-command";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, conflicting));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(beforeConflict.Count + 1);
        committed[^1].EventData.Unpack<NyxIdChatTurnAdmissionRejectedEvent>()
            .ReasonCode.Should().Be("IDEMPOTENCY_CONFLICT");
    }

    [Theory]
    [InlineData(
        "reserve",
        "NYXID_CHAT_HISTORY_RESERVATION_FAILED",
        "history.reserve")]
    [InlineData(
        "create",
        "NYXID_CHAT_TURN_ACTOR_CREATE_FAILED",
        "history.reserve,create")]
    [InlineData(
        "link",
        "NYXID_CHAT_TURN_ACTOR_LINK_FAILED",
        "history.reserve,create,link")]
    [InlineData(
        "dispatch",
        "NYXID_CHAT_OPERATION_DISPATCH_FAILED",
        "history.reserve,create,link,dispatch")]
    [InlineData(
        "dispatch-rejected",
        "NYXID_CHAT_OPERATION_DISPATCH_REJECTED",
        "history.reserve,create,link,dispatch")]
    public async Task StartTurn_WhenFirstDispatchStageFails_ShouldCommitSafeTerminalFailure(
        string failedStage,
        string expectedFailureCode,
        string expectedOperations)
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations)
        {
            ReserveException = failedStage == "reserve"
                ? new InvalidOperationException("reserve failed with bearer-secret")
                : null,
        };
        var runtime = new RecordingActorRuntime(operations, failedStage);
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask,
            failedStage);
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            runtime,
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();

        var act = () => agent.HandleEventAsync(
            CreateEnvelope(conversationActorId, CreateStartTurnCommand()));

        await act.Should().NotThrowAsync();
        operations.Should().Equal(expectedOperations.Split(','));
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        var step = agent.State.ActiveTask.Steps.Should().ContainSingle().Which;
        step.Status.Should().Be(NyxIdChatStepStatus.Failed);
        step.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Failed);
        step.FailureCode.Should().Be(expectedFailureCode);
        step.ExternalEffect.Should().BeOneOf(
            NyxIdChatEffectEvidence.NotStarted,
            NyxIdChatEffectEvidence.NotApplied);
        agent.State.ToString().Should()
            .NotContain("bearer-secret")
            .And.NotContain("credential");

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed[^1].EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor)
            .Should().BeTrue();
        committed.Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor));
        var failed = committed[^1].EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        failed.Result.Failure.FailureCode.Should().Be(expectedFailureCode);
        failed.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        failed.ToString().Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task StartTurn_WhenReservationIsCancelled_ShouldNotCommitBusinessFailure()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations)
        {
            ReserveException = new OperationCanceledException("request cancelled"),
        };
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();

        var act = () => agent.HandleEventAsync(
            CreateEnvelope(conversationActorId, CreateStartTurnCommand()));

        await act.Should().ThrowAsync<OperationCanceledException>();
        operations.Should().Equal("history.reserve");
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should()
            .Be(NyxIdChatOperationPhase.Requested);
        (await eventStore.GetEventsAsync(conversationActorId)).Should().ContainSingle()
            .Which.EventData.Is(NyxIdChatTurnStartedEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ActivateAsync_WithPendingReservation_ShouldReserveBeforePublishingOperationRecovery()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations)
        {
            ReserveException = new OperationCanceledException("crash after turn commit"),
        };
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, history);
        var initial = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(initial, conversationActorId);
        await initial.ActivateAsync();
        await FluentActions.Invoking(() => initial.HandleEventAsync(
                CreateEnvelope(conversationActorId, CreateStartTurnCommand())))
            .Should().ThrowAsync<OperationCanceledException>();
        var pending = initial.State.HistoryDeliveryReservation.Clone();
        pending.Dispatched.Should().BeFalse();

        history.ReserveException = null;
        history.Reservations.Clear();
        operations.Clear();
        var recoveryDispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var recovered = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            recoveryDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(recovered, conversationActorId);

        await recovered.ActivateAsync();

        operations.Should().Equal("history.reserve", "dispatch");
        var replayedReservation = history.Reservations.Should().ContainSingle().Which;
        replayedReservation.Should().BeEquivalentTo(new ChatHistoryTurnDeliveryReservation(
            pending.DeliveryId,
            pending.ScopeId,
            pending.ConversationId,
            pending.TurnId,
            pending.UserText,
            pending.SourceActorId,
            pending.SourceCommandId,
            pending.SourceCorrelationId,
            pending.RequestFingerprint,
            pending.CreateConversationIfMissing,
            pending.ExposeCreateRecovery));
        recovered.State.HistoryDeliveryReservation.Dispatched.Should().BeTrue();
        var events = await eventStore.GetEventsAsync(conversationActorId);
        events.Select(static item => item.EventData.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnStartedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatHistoryDeliveryReservationDispatchedEvent()).TypeUrl);
        var recovery = recoveryDispatch.Calls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatRecoveryRequestedSignal>();
        recovery.Key.OperationId.Should().Be(
            recovered.State.ActiveTask.Steps.Single().Operation.Key.OperationId);
        recovery.ExpectedStateVersion.Should().Be(events[^1].Version);
    }

    [Fact]
    public async Task ActivateAsync_WhenPendingReservationRecoveryFails_ShouldScheduleRetryAndContinueOperationRecovery()
    {
        const string conversationActorId = "conversation-alpha";
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations)
        {
            ReserveException = new OperationCanceledException("crash after turn commit"),
        };
        var eventStore = new InMemoryEventStoreForTests();
        var callbacks = new RecordingRuntimeCallbackScheduler();
        using var services = BuildEventSourcingServices(eventStore, history, callbacks);
        var initial = CreateController(services, conversationActorId);
        await initial.ActivateAsync();
        await FluentActions.Invoking(() => initial.HandleEventAsync(
                CreateEnvelope(conversationActorId, CreateStartTurnCommand())))
            .Should().ThrowAsync<OperationCanceledException>();

        history.ReserveException = new InvalidOperationException("history unavailable");
        operations.Clear();
        var recoveryDispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var recovered = CreateController(services, conversationActorId, recoveryDispatch);

        await recovered.ActivateAsync();

        recovered.State.HistoryDeliveryReservation.Dispatched.Should().BeFalse();
        callbacks.TimeoutRequests.Should().ContainSingle();
        recoveryDispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor),
            "history recovery failure must not suppress operation recovery");

        history.ReserveException = null;
        await recovered.HandleEventAsync(callbacks.TimeoutRequests.Single().TriggerEnvelope.Clone());

        recovered.State.HistoryDeliveryReservation.Dispatched.Should().BeTrue();
    }

    [Fact]
    public async Task ChildResult_ShouldBecomeProductFactOnlyAfterCompleteKeyReconciliationCommit()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var mismatched = new NyxIdChatOperationResultSignal
        {
            Key = key.Clone(),
            Llm = new NyxIdChatLLMOperationResult { Content = "must be ignored" },
        };
        mismatched.Key.OperationId = "operation-wrong";

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, mismatched));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(afterStart.Count);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        agent.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Running);

        var accepted = new NyxIdChatOperationResultSignal
        {
            Key = key,
            Llm = new NyxIdChatLLMOperationResult { Content = "completed" },
        };
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, accepted));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(afterStart.Count + 1);
        committed[^1].EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor).Should().BeTrue();
        var reconciliation = committed[^1].EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        reconciliation.Result.Key.Should().BeEquivalentTo(key);
        reconciliation.Task.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        reconciliation.Turn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        agent.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Done);
    }

    [Theory]
    [InlineData(NyxIdChatTurnStatus.Succeeded)]
    [InlineData(NyxIdChatTurnStatus.Failed)]
    [InlineData(NyxIdChatTurnStatus.Stopped)]
    [InlineData(NyxIdChatTurnStatus.Blocked)]
    public async Task TerminalTransition_ShouldAtomicallyPrepareSafeHistoryOutbox(
        NyxIdChatTurnStatus terminalStatus)
    {
        const string conversationActorId = "conversation-alpha";
        const string reasoningSecret = "reasoning-secret-sentinel";
        const string toolArgumentSecret = "tool-argument-secret-sentinel";
        const string inlineSecret = "inline-secret-sentinel";
        var eventStore = new InMemoryEventStoreForTests();
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations);
        using var services = BuildEventSourcingServices(eventStore, history);
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var reservation = agent.State.HistoryDeliveryReservation.Clone();
        var firstKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();

        switch (terminalStatus)
        {
            case NyxIdChatTurnStatus.Succeeded:
                await agent.HandleEventAsync(CreateEnvelope(
                    conversationActorId,
                    new NyxIdChatOperationResultSignal
                    {
                        Key = firstKey,
                        Llm = new NyxIdChatLLMOperationResult
                        {
                            Content = "Final assistant answer.",
                            ReasoningContent = reasoningSecret,
                            ContentParts =
                            {
                                new ChatContentPart
                                {
                                    Kind = ChatContentPartKind.Image,
                                    DataBase64 = inlineSecret,
                                    MediaType = "image/png",
                                },
                            },
                        },
                    }));
                break;
            case NyxIdChatTurnStatus.Failed:
                await agent.HandleEventAsync(CreateEnvelope(
                    conversationActorId,
                    new NyxIdChatOperationResultSignal
                    {
                        Key = firstKey,
                        Failure = new NyxIdChatOperationFailure
                        {
                            FailureCode = "MODEL_FAILED",
                            SafeMessage = "The model attempt failed safely.",
                            ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
                        },
                    }));
                break;
            case NyxIdChatTurnStatus.Stopped:
            {
                var events = await eventStore.GetEventsAsync(conversationActorId);
                await agent.HandleEventAsync(CreateEnvelope(
                    conversationActorId,
                    CreateStopCommand(events[^1].Version)));
                break;
            }
            case NyxIdChatTurnStatus.Blocked:
                await agent.HandleEventAsync(CreateEnvelope(
                    conversationActorId,
                    new NyxIdChatOperationResultSignal
                    {
                        Key = firstKey,
                        Llm = new NyxIdChatLLMOperationResult
                        {
                            ToolCalls =
                            {
                                new NyxIdChatToolCall
                                {
                                    CallId = "call-connect-alpha",
                                    ToolName = "nyxid_proxy",
                                    ArgumentsJson = JsonSerializer.Serialize(new
                                    {
                                        secret = toolArgumentSecret,
                                    }),
                                    Safety = new NyxIdChatToolCallSafety(),
                                },
                            },
                        },
                    }));
                var toolKey = agent.State.ActiveTask.Steps.Last().Operation.Key.Clone();
                await agent.HandleEventAsync(CreateEnvelope(
                    conversationActorId,
                    new NyxIdChatOperationResultSignal
                    {
                        Key = toolKey,
                        Tool = new NyxIdChatToolOperationResult
                        {
                            ResultJson = JsonSerializer.Serialize(new
                            {
                                secret = inlineSecret,
                            }),
                            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                            Receipt = new AgentToolReceipt
                            {
                                CallId = "call-connect-alpha",
                                ToolName = "nyxid_proxy",
                                Status = AgentToolReceiptStatus.AuthorizationRequired,
                                AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                                {
                                    ServiceSlug = "api-github",
                                    ReasonCode = "NYXID_UNAUTHORIZED",
                                    SafeMessage = "Connect GitHub.",
                                },
                            },
                        },
                    }));
                break;
        }

        agent.State.ActiveTurn.Status.Should().Be(terminalStatus);
        var outbox = agent.State.PendingHistoryTerminal;
        outbox.Should().NotBeNull();
        outbox.DeliveryId.Should().Be(reservation.DeliveryId);
        outbox.SourceActorId.Should().Be(conversationActorId);
        outbox.SourceCommandId.Should().Be("command-alpha");
        outbox.Status.Should().Be(terminalStatus);
        outbox.Attempt.Should().Be(1);
        outbox.ObservedAt.Should().NotBeNull();
        switch (terminalStatus)
        {
            case NyxIdChatTurnStatus.Succeeded:
                outbox.Text.Should().Be("Final assistant answer.");
                outbox.ErrorCode.Should().BeEmpty();
                break;
            case NyxIdChatTurnStatus.Failed:
                outbox.Text.Should().Be("The model attempt failed safely.");
                outbox.ErrorCode.Should().Be("MODEL_FAILED");
                break;
            case NyxIdChatTurnStatus.Stopped:
                outbox.Text.Should().BeEmpty();
                outbox.ErrorCode.Should().Be(NyxIdChatControlCommands.StopUncancellable);
                break;
            case NyxIdChatTurnStatus.Blocked:
                outbox.Text.Should().NotBeNullOrWhiteSpace();
                outbox.ErrorCode.Should().Be(NyxIdChatBrowserActions.ActionRequested);
                break;
        }

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var committedState = UnpackControllerState(committed[^1]);
        committedState.PendingHistoryTerminal.ToByteString().Should()
            .Equal(outbox.ToByteString(),
                "the terminal and its delivery outbox must be one committed fact");
        committed[^1].EventData.ToString().Should()
            .NotContain(reasoningSecret)
            .And.NotContain(toolArgumentSecret)
            .And.NotContain(inlineSecret)
            .And.NotContain("runtime-token-alpha");
        dispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor));
    }

    [Theory]
    [InlineData(
        NyxIdChatTurnStatus.Succeeded,
        ChatHistoryTurnTerminalStatus.Completed,
        "Final assistant answer.",
        "")]
    [InlineData(
        NyxIdChatTurnStatus.Failed,
        ChatHistoryTurnTerminalStatus.Failed,
        "The model attempt failed safely.",
        "MODEL_FAILED")]
    [InlineData(
        NyxIdChatTurnStatus.Stopped,
        ChatHistoryTurnTerminalStatus.Stopped,
        "",
        "TURN_STOPPED")]
    [InlineData(
        NyxIdChatTurnStatus.Blocked,
        ChatHistoryTurnTerminalStatus.Blocked,
        "Connect GitHub.",
        "ACTION_REQUIRED")]
    public async Task HistoryTerminalSelfSignal_ShouldNotifyMappedTerminalAndClearMatchingOutbox(
        NyxIdChatTurnStatus controllerStatus,
        ChatHistoryTurnTerminalStatus historyStatus,
        string text,
        string errorCode)
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(
            eventStore,
            conversationActorId,
            version: 1,
            CreatePendingHistoryTerminalState(controllerStatus, text, errorCode));
        var history = new RecordingChatHistoryCommandPort([]);
        using var services = BuildEventSourcingServices(eventStore, history);
        var dispatch = new RecordingActorDispatchPort(
            [],
            static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);

        await agent.ActivateAsync();

        var selfEnvelope = dispatch.Calls.Should().ContainSingle().Which.Envelope.Clone();
        var signal = selfEnvelope.Payload
            .Unpack<NyxIdChatHistoryTerminalDispatchRequested>();
        signal.DeliveryId.Should().Be("delivery-terminal-alpha");
        signal.Attempt.Should().Be(1);

        await agent.HandleEventAsync(selfEnvelope);

        history.Notifications.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ChatHistoryTurnTerminalNotification(
                "delivery-terminal-alpha",
                conversationActorId,
                "command-terminal-alpha",
                historyStatus,
                text,
                errorCode,
                new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)));
        agent.State.PendingHistoryTerminal.Should().BeNull();
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed[^1].EventData.Is(NyxIdChatHistoryTerminalDispatchedEvent.Descriptor)
            .Should().BeTrue();

        await agent.HandleEventAsync(selfEnvelope.Clone());

        history.Notifications.Should().ContainSingle(
            "an exact terminal self-message replay is a no-op after the marker commits");
        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(committed.Count);
    }

    [Fact]
    public async Task HistoryTerminalSelfSignal_WhenNotificationFails_ShouldScheduleMinimalRetryAndRecover()
    {
        const string conversationActorId = "conversation-alpha";
        const string terminalText = "safe-terminal-sentinel";
        const string credentialSecret = "credential-secret-sentinel";
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(
            eventStore,
            conversationActorId,
            version: 1,
            CreatePendingHistoryTerminalState(
                NyxIdChatTurnStatus.Failed,
                terminalText,
                "MODEL_FAILED"));
        var history = new RecordingChatHistoryCommandPort([])
        {
            NotifyException = new InvalidOperationException(credentialSecret),
        };
        var callbacks = new RecordingRuntimeCallbackScheduler();
        using var services = BuildEventSourcingServices(eventStore, history, callbacks);
        var firstDispatch = new RecordingActorDispatchPort(
            [],
            static (_, _) => Task.CompletedTask);
        var initial = CreateController(services, conversationActorId, firstDispatch);
        await initial.ActivateAsync();
        var firstSignal = firstDispatch.Calls.Should().ContainSingle().Which.Envelope.Clone();

        await initial.HandleEventAsync(firstSignal);

        history.Notifications.Should().ContainSingle();
        initial.State.PendingHistoryTerminal.Should().NotBeNull();
        initial.State.PendingHistoryTerminal.Attempt.Should().Be(2);
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed[^1].EventData.Is(NyxIdChatHistoryTerminalRetryScheduledEvent.Descriptor)
            .Should().BeTrue();
        committed[^1].EventData.ToString().Should()
            .NotContain(terminalText)
            .And.NotContain(credentialSecret);

        var timeout = callbacks.TimeoutRequests.Should().ContainSingle().Which;
        timeout.ActorId.Should().Be(conversationActorId);
        timeout.DueTime.Should().BePositive();
        timeout.TriggerEnvelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        var retrySignal = timeout.TriggerEnvelope.Payload
            .Unpack<NyxIdChatHistoryTerminalDispatchRequested>();
        retrySignal.DeliveryId.Should().Be("delivery-terminal-alpha");
        retrySignal.Attempt.Should().Be(2);
        timeout.TriggerEnvelope.ToString().Should()
            .NotContain(terminalText)
            .And.NotContain(credentialSecret);

        var recoveryDispatch = new RecordingActorDispatchPort(
            [],
            static (_, _) => Task.CompletedTask);
        var recovered = CreateController(services, conversationActorId, recoveryDispatch);
        await recovered.ActivateAsync();
        var recoveredSignal = recoveryDispatch.Calls.Should().ContainSingle().Which.Envelope.Clone();
        recoveredSignal.Payload.ToByteString().Should().Equal(
            timeout.TriggerEnvelope.Payload.ToByteString(),
            "activation and durable retry must address the same pending attempt");

        history.NotifyException = null;
        await recovered.HandleEventAsync(recoveredSignal);
        recovered.State.PendingHistoryTerminal.Should().BeNull();
        history.Notifications.Should().HaveCount(2);

        await recovered.HandleEventAsync(timeout.TriggerEnvelope.Clone());
        history.Notifications.Should().HaveCount(2,
            "the durable callback is stale after successful admission commits");
    }

    [Fact]
    public async Task AuthorizationRequiredResult_ShouldAtomicallyCommitBlockedActionRequest()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = llmKey,
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-connect-alpha",
                        ToolName = "nyxid_proxy",
                        ArgumentsJson = "{}",
                        Safety = new NyxIdChatToolCallSafety(),
                    },
                },
            },
        }));
        var toolKey = agent.State.ActiveTask.Steps.Last().Operation.Key.Clone();
        var beforeAuthorization = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = toolKey,
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-connect-alpha",
                    ToolName = "nyxid_proxy",
                    Status = AgentToolReceiptStatus.AuthorizationRequired,
                    ErrorCode = "NYXID_UNAUTHORIZED",
                    ErrorMessage = "Connect or reauthorize api-github to continue.",
                    AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                    {
                        ServiceSlug = "api-github",
                        ReasonCode = "NYXID_UNAUTHORIZED",
                        SafeMessage = "Connect or reauthorize api-github to continue.",
                    },
                },
            },
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(beforeAuthorization.Count + 1,
            "the waiting tool result and browser handoff are one authoritative commit");
        committed[^1].EventData.Is(NyxIdChatActionRequestedEvent.Descriptor).Should().BeTrue();
        var requested = committed[^1].EventData.Unpack<NyxIdChatActionRequestedEvent>();
        requested.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Blocked);
        requested.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Blocked);
        requested.State.PendingActions.Should().ContainSingle();
        requested.Request.Should().BeEquivalentTo(requested.State.PendingActions.Single());
        requested.Task.Should().BeEquivalentTo(requested.State.ActiveTask);
        requested.OriginTurn.Should().BeEquivalentTo(requested.State.ActiveTurn);
        agent.State.Should().BeEquivalentTo(requested.State);
        agent.State.ActiveTask.Steps.Should().Contain(step =>
            step.StepId == toolKey.StepId &&
            step.Status == NyxIdChatStepStatus.Waiting);
        agent.State.ActiveTask.Steps.Should().Contain(step =>
            step.Kind == NyxIdChatStepKind.BrowserAction &&
            step.Status == NyxIdChatStepStatus.Waiting);
    }

    [Fact]
    public async Task AuthorizationRequiredResult_WhenActionRegistryIsDisabled_ShouldCommitTypedFailure()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(
            eventStore,
            actionRegistry: NyxIdAssistantActionRegistry.CreateDisabled());
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = llmKey,
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-connect-alpha",
                        ToolName = "nyxid_require_service",
                        ArgumentsJson = "{}",
                        Safety = new NyxIdChatToolCallSafety(),
                    },
                },
            },
        }));
        var toolKey = agent.State.ActiveTask.Steps.Last().Operation.Key.Clone();

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = toolKey,
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-connect-alpha",
                    ToolName = "nyxid_require_service",
                    Status = AgentToolReceiptStatus.AuthorizationRequired,
                    AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                    {
                        ServiceSlug = "api-github",
                        ReasonCode = "NYXID_SERVICE_REGISTRATION_REQUIRED",
                        SafeMessage = "Connect the requested service.",
                    },
                },
            },
        }));

        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        agent.State.ActiveTurn.FailureCode.Should().Be("NYXID_ACTION_UNSUPPORTED");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        agent.State.PendingActions.Should().BeEmpty();
        agent.State.PendingHistoryTerminal.ErrorCode.Should().Be("NYXID_ACTION_UNSUPPORTED");
        dispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor));
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().Contain(stateEvent =>
            stateEvent.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor) &&
            stateEvent.EventData.Unpack<NyxIdChatOperationReconciledEvent>()
                .State.ActiveTurn.FailureCode == "NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public async Task ActionContinuation_ShouldCommitPostconditionWaterlineBeforeDispatch()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var blocked = CreateBlockedActionState();
        await PersistActionStateAsync(eventStore, conversationActorId, blocked);
        IReadOnlyList<StateEvent>? eventsObservedAtDispatch = null;
        NyxIdChatConversationGAgentState? stateObservedAtDispatch = null;
        IReadOnlyList<StateEvent>? eventsObservedAtReservation = null;
        NyxIdChatConversationGAgent? agent = null;
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(
            operations,
            async _ =>
            {
                eventsObservedAtReservation = await eventStore.GetEventsAsync(
                    conversationActorId);
            });
        var dispatch = new RecordingActorDispatchPort(
            operations,
            async (_, envelope) =>
            {
                envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor).Should().BeTrue();
                eventsObservedAtDispatch = await eventStore.GetEventsAsync(conversationActorId);
                stateObservedAtDispatch = agent!.State.Clone();
            });
        var runtime = new RecordingActorRuntime(operations);
        using var services = BuildEventSourcingServices(eventStore, history);
        agent = new NyxIdChatConversationGAgent(
            runtime,
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        var command = CreateActionContinueCommand(blocked.PendingActions.Single().ActionRequestId);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));

        operations.Should().Equal("history.reserve", "create", "link", "dispatch");
        var reservation = history.Reservations.Should().ContainSingle().Which;
        reservation.ScopeId.Should().Be("scope-alpha");
        reservation.ConversationId.Should().Be(conversationActorId);
        reservation.TurnId.Should().Be("turn-action-alpha");
        reservation.UserText.Should().Be("NyxID action update: completed.");
        reservation.UserText.Should().NotContain("service-alpha");
        reservation.SourceActorId.Should().Be(conversationActorId);
        reservation.SourceCommandId.Should().Be("command-action-alpha");
        reservation.SourceCorrelationId.Should().Be("correlation-action-alpha");
        reservation.CreateConversationIfMissing.Should().BeTrue();
        reservation.ExposeCreateRecovery.Should().BeFalse();
        eventsObservedAtReservation.Should().NotBeNull();
        eventsObservedAtReservation![^1].EventData.Is(
            NyxIdChatContinuationAdmissionCommittedEvent.Descriptor).Should().BeTrue();
        eventsObservedAtDispatch.Should().NotBeNull();
        eventsObservedAtDispatch![^1].EventData.Is(
            NyxIdChatHistoryDeliveryReservationDispatchedEvent.Descriptor).Should().BeTrue();
        stateObservedAtDispatch.Should().NotBeNull();
        stateObservedAtDispatch!.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
        stateObservedAtDispatch.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        var postcondition = stateObservedAtDispatch.ActiveTask.Steps.Should()
            .ContainSingle().Which;
        postcondition.Kind.Should().Be(NyxIdChatStepKind.Postcondition);
        postcondition.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        stateObservedAtDispatch.HistoryDeliveryReservation.DeliveryId.Should().Be(
            reservation.DeliveryId);
        stateObservedAtDispatch.HistoryDeliveryReservation.TurnId.Should().Be(
            "turn-action-alpha");
        stateObservedAtDispatch.HistoryDeliveryReservation.Dispatched.Should().BeTrue();
        dispatch.Calls.Should().ContainSingle();
        var dispatched = dispatch.Calls.Single().Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        dispatched.Key.Should().BeEquivalentTo(postcondition.Operation.Key);
        dispatched.ActionPostcondition.OwnerSubject.Should().Be("owner-alpha");
        dispatched.ActionPostcondition.OriginTurnId.Should().Be("turn-alpha");

        var all = await eventStore.GetEventsAsync(conversationActorId);
        all[^1].EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor).Should().BeTrue();
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command.Clone()));

        history.Reservations.Should().ContainSingle(
            "an exact continuation replay cannot reserve the same turn twice");
        runtime.CreateCalls.Should().ContainSingle();
        runtime.LinkCalls.Should().ContainSingle();
        dispatch.OperationCalls.Should().ContainSingle(
            "the committed dispatched waterline makes an exact replay a no-op");
        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(all.Count);
    }

    [Fact]
    public async Task EmptyActionWakeWithoutPendingActions_ShouldCommitAndPrepareTerminal()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var terminal = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = conversationActorId,
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Succeeded,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Succeeded,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTaskStatus.Succeeded,
            },
            ProgressSequence = 3,
        };
        await PersistTestStateAsync(eventStore, conversationActorId, 1, terminal);
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations);
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var command = CreateActionContinueCommand("unused-action");
        command.OriginTurnId = string.Empty;
        command.Actions.Clear();

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));

        operations.Should().Equal("history.reserve");
        agent.State.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        agent.State.ActiveTask.Steps.Should().BeEmpty();
        agent.State.PendingHistoryTerminal.Should().NotBeNull();
        agent.State.PendingHistoryTerminal.TurnId.Should().Be("turn-action-alpha");
        history.Reservations.Should().ContainSingle().Which.UserText.Should().Be(
            "NyxID state changed; recheck pending actions.");
        dispatch.OperationCalls.Should().BeEmpty();
        dispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor));
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().Contain(stateEvent =>
            stateEvent.EventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor) &&
            stateEvent.EventData.Unpack<NyxIdChatContinuationAdmissionCommittedEvent>()
                .State.ActiveTurn.Status == NyxIdChatTurnStatus.Succeeded);
        var stateAfterFirst = agent.State.ToByteArray();
        var operationsAfterFirst = operations.ToArray();
        var eventCountAfterFirst = committed.Count;
        var reservationCountAfterFirst = history.Reservations.Count;
        var dispatchCountAfterFirst = dispatch.Calls.Count;

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command.Clone()));

        (await eventStore.GetEventsAsync(conversationActorId)).Should()
            .HaveCount(eventCountAfterFirst);
        agent.State.ToByteArray().Should().Equal(stateAfterFirst);
        operations.Should().Equal(operationsAfterFirst);
        history.Reservations.Should().HaveCount(reservationCountAfterFirst);
        dispatch.Calls.Should().HaveCount(dispatchCountAfterFirst);
    }

    [Theory]
    [InlineData(
        "reserve",
        "NYXID_CHAT_HISTORY_RESERVATION_FAILED",
        "history.reserve")]
    [InlineData(
        "create",
        "NYXID_CHAT_TURN_ACTOR_CREATE_FAILED",
        "history.reserve,create")]
    [InlineData(
        "link",
        "NYXID_CHAT_TURN_ACTOR_LINK_FAILED",
        "history.reserve,create,link")]
    [InlineData(
        "dispatch",
        "NYXID_CHAT_OPERATION_DISPATCH_FAILED",
        "history.reserve,create,link,dispatch")]
    [InlineData(
        "dispatch-rejected",
        "NYXID_CHAT_OPERATION_DISPATCH_REJECTED",
        "history.reserve,create,link,dispatch")]
    public async Task ActionContinuation_WhenFirstDispatchStageFails_ShouldCommitSafeTerminal(
        string failedStage,
        string expectedFailureCode,
        string expectedOperations)
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var blocked = CreateBlockedActionState();
        await PersistActionStateAsync(eventStore, conversationActorId, blocked);
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations)
        {
            ReserveException = failedStage == "reserve"
                ? new InvalidOperationException("reserve failed with bearer-secret")
                : null,
        };
        var runtime = new RecordingActorRuntime(operations, failedStage);
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask,
            failedStage);
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            runtime,
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();

        var act = () => agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateActionContinueCommand(blocked.PendingActions.Single().ActionRequestId)));

        await act.Should().NotThrowAsync();
        operations.Should().Equal(expectedOperations.Split(','));
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        var step = agent.State.ActiveTask.Steps.Should().ContainSingle().Which;
        step.Status.Should().Be(NyxIdChatStepStatus.Failed);
        step.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Failed);
        step.FailureCode.Should().Be(expectedFailureCode);
        step.ExternalEffect.Should().BeOneOf(
            NyxIdChatEffectEvidence.NotStarted,
            NyxIdChatEffectEvidence.NotApplied);
        agent.State.PendingHistoryTerminal.Should().NotBeNull();
        agent.State.PendingHistoryTerminal.TurnId.Should().Be("turn-action-alpha");
        agent.State.PendingHistoryTerminal.SourceCommandId.Should().Be(
            "command-action-alpha");
        agent.State.PendingHistoryTerminal.ErrorCode.Should().Be(expectedFailureCode);
        agent.State.ToString().Should().NotContain("bearer-secret");

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed[^1].EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor)
            .Should().BeTrue();
        committed.Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor));
        var failed = committed[^1].EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        failed.Result.Failure.FailureCode.Should().Be(expectedFailureCode);
        failed.State.PendingHistoryTerminal.Should().NotBeNull();
        failed.ToString().Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task ActionContinuation_WhenReservationFails_ShouldRecoverReservationBeforeTerminalDelivery()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var blocked = CreateBlockedActionState();
        await PersistActionStateAsync(eventStore, conversationActorId, blocked);
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations)
        {
            ReserveException = new InvalidOperationException("reservation unavailable"),
        };
        var initialDispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, history);
        var initial = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            initialDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(initial, conversationActorId);
        await initial.ActivateAsync();

        await initial.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateActionContinueCommand(blocked.PendingActions.Single().ActionRequestId)));

        initial.State.HistoryDeliveryReservation.Dispatched.Should().BeFalse();
        initial.State.PendingHistoryTerminal.Should().NotBeNull();
        initialDispatch.Calls.Should().NotContain(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor),
            "terminal delivery cannot overtake its failed reservation");

        history.ReserveException = null;
        history.Reservations.Clear();
        operations.Clear();
        var recoveryDispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var recovered = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            recoveryDispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(recovered, conversationActorId);

        await recovered.ActivateAsync();

        operations.Should().Equal("history.reserve");
        history.Reservations.Should().ContainSingle();
        recovered.State.HistoryDeliveryReservation.Dispatched.Should().BeTrue();
        recoveryDispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor));
        recoveryDispatch.Calls.Should().NotContain(call =>
            call.Envelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor),
            "a terminal action continuation has no operation left to recover");
    }

    [Fact]
    public async Task NonCompletedActionContinuation_ShouldReserveBeforePublishingTerminal()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var blocked = CreateBlockedActionState();
        await PersistActionStateAsync(eventStore, conversationActorId, blocked);
        IReadOnlyList<StateEvent>? eventsObservedAtReservation = null;
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(
            operations,
            async _ =>
            {
                eventsObservedAtReservation = await eventStore.GetEventsAsync(
                    conversationActorId);
            });
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        var command = CreateActionContinueCommand(
            blocked.PendingActions.Single().ActionRequestId);
        command.Actions.Single().Disposition = NyxIdChatActionDisposition.Declined;
        command.Actions.Single().Resource = null;

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));

        operations.Should().Equal("history.reserve");
        var reservation = history.Reservations.Should().ContainSingle().Which;
        reservation.UserText.Should().Be("NyxID action update: declined.");
        eventsObservedAtReservation.Should().NotBeNull();
        eventsObservedAtReservation![^1].EventData.Is(
            NyxIdChatContinuationAdmissionCommittedEvent.Descriptor).Should().BeTrue();
        agent.State.HistoryDeliveryReservation.Dispatched.Should().BeTrue();
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        agent.State.PendingHistoryTerminal.Should().NotBeNull();
        agent.State.PendingHistoryTerminal.DeliveryId.Should().Be(reservation.DeliveryId);
        agent.State.PendingHistoryTerminal.ErrorCode.Should().Be("NYXID_ACTION_DECLINED");
        dispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor));
    }

    [Fact]
    public async Task ActionPostconditionTerminal_ShouldPrepareContinuationHistoryOutbox()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var blocked = CreateBlockedActionState();
        await PersistActionStateAsync(eventStore, conversationActorId, blocked);
        var operations = new List<string>();
        var history = new RecordingChatHistoryCommandPort(operations);
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        var command = CreateActionContinueCommand(
            blocked.PendingActions.Single().ActionRequestId);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));
        var deliveryId = history.Reservations.Should().ContainSingle().Which.DeliveryId;
        var postconditionKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = postconditionKey,
                ActionPostcondition = new NyxIdChatActionPostconditionResult
                {
                    ActionRequestId = blocked.PendingActions.Single().ActionRequestId,
                    Disposition = NyxIdChatActionDisposition.Completed,
                    Verified = true,
                    Resource = new NyxIdChatSafeResourceRef
                    {
                        UserService = new NyxIdChatUserServiceRef
                        {
                            UserServiceId = "service-alpha",
                        },
                    },
                },
            }));

        agent.State.ActiveTurn.TurnId.Should().Be("turn-action-alpha");
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        agent.State.PendingHistoryTerminal.Should().NotBeNull();
        agent.State.PendingHistoryTerminal.DeliveryId.Should().Be(deliveryId);
        agent.State.PendingHistoryTerminal.TurnId.Should().Be("turn-action-alpha");
        agent.State.PendingHistoryTerminal.SourceCommandId.Should().Be(
            "command-action-alpha");
        agent.State.PendingHistoryTerminal.Status.Should().Be(
            NyxIdChatTurnStatus.Succeeded);
        agent.State.PendingHistoryTerminal.Text.Should().BeEmpty(
            "a postcondition result is not assistant-authored chat content");
        agent.State.HistoryDeliveryReservation.ToString().Should()
            .NotContain("service-alpha");
        agent.State.PendingHistoryTerminal.ToString().Should()
            .NotContain("service-alpha");
        dispatch.Calls.Should().ContainSingle(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor));
    }

    [Fact]
    public async Task ActiveTurnActionContinuation_ShouldCommitTypedRejectionWithoutDispatch()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var blocked = CreateBlockedActionState();
        await PersistActionStateAsync(eventStore, conversationActorId, blocked);
        var activeWithPendingAction = blocked.Clone();
        activeWithPendingAction.ActiveTurn = new NyxIdChatTurnState
        {
            TurnId = "turn-other-active",
            TaskId = "task-other-active",
            Status = NyxIdChatTurnStatus.Active,
        };
        activeWithPendingAction.LatestTurn = activeWithPendingAction.ActiveTurn.Clone();
        activeWithPendingAction.ActiveTask = new NyxIdChatTaskState
        {
            TurnId = "turn-other-active",
            TaskId = "task-other-active",
            Status = NyxIdChatTaskStatus.Active,
        };
        await PersistTestStateAsync(
            eventStore,
            conversationActorId,
            version: 2,
            activeWithPendingAction);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateActionContinueCommand(blocked.PendingActions.Single().ActionRequestId)));

        dispatch.Calls.Should().BeEmpty();
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed[^1].EventData.Is(
            NyxIdChatContinuationAdmissionCommittedEvent.Descriptor).Should().BeTrue();
        var rejected = committed[^1].EventData
            .Unpack<NyxIdChatContinuationAdmissionCommittedEvent>();
        rejected.Admission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Rejected);
        rejected.Admission.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationActiveTurn);
        rejected.State.ActiveTurn.TurnId.Should().Be("turn-other-active");
        rejected.State.PendingActions.Should().ContainSingle();
        agent.State.Should().BeEquivalentTo(rejected.State);
    }

    [Fact]
    public async Task ConflictingActionContinuationRetry_ShouldCommitTypedRejectionForCurrentTurn()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var blocked = CreateBlockedActionState();
        await PersistActionStateAsync(eventStore, conversationActorId, blocked);
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var first = CreateActionContinueCommand(blocked.PendingActions.Single().ActionRequestId);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, first));
        var beforeConflict = await eventStore.GetEventsAsync(conversationActorId);
        var conflicting = first.Clone();
        conflicting.Actions.Single().Disposition = NyxIdChatActionDisposition.Declined;
        conflicting.Actions.Single().Resource = null;

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, conflicting));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(beforeConflict.Count + 1);
        var rejected = committed[^1].EventData
            .Unpack<NyxIdChatTurnAdmissionRejectedEvent>();
        rejected.RequestedTurnId.Should().Be(first.ContinuationTurnId);
        rejected.ActiveTurnId.Should().Be(first.ContinuationTurnId);
        rejected.ReasonCode.Should().Be(
            NyxIdChatBrowserActions.ActionContinuationConflict);
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        dispatch.OperationCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task LaterOrdinaryTurn_ShouldPreservePendingAndRecentActions()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var state = CreateBlockedActionState();
        var recent = state.PendingActions.Single().Clone();
        recent.ActionRequestId = "action-recent-alpha";
        recent.StepId = "step-recent-alpha";
        state.RecentActions.Add(recent);
        state.LatestInputResolution = new NyxIdChatInputResolutionState
        {
            RequestId = "input-resolved-alpha",
            ClientRequestId = "client-input-resolved-alpha",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            AnswerSha256 = ByteString.CopyFromUtf8("input-fingerprint"),
        };
        state.RecentInputResolutions.Add(state.LatestInputResolution.Clone());
        state.LatestApprovalResolution = new NyxIdChatApprovalResolutionState
        {
            RequestId = "approval-resolved-alpha",
            ClientRequestId = "client-approval-resolved-alpha",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            Approved = true,
            DecisionSha256 = ByteString.CopyFromUtf8("approval-fingerprint"),
        };
        state.RecentApprovalResolutions.Add(state.LatestApprovalResolution.Clone());
        await PersistActionStateAsync(eventStore, conversationActorId, state);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        var nextTurn = CreateStartTurnCommand();
        nextTurn.TurnId = "turn-beta";
        nextTurn.TaskId = "task-beta";
        nextTurn.ClientRequestId = "client-beta";
        nextTurn.CommandId = "command-beta";
        nextTurn.CorrelationId = "correlation-beta";

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, nextTurn));

        agent.State.ActiveTurn.TurnId.Should().Be("turn-beta");
        agent.State.PendingActions.Should().ContainSingle(action =>
            action.ActionRequestId == state.PendingActions.Single().ActionRequestId);
        agent.State.RecentActions.Should().ContainSingle(action =>
            action.ActionRequestId == "action-recent-alpha");
        agent.State.LatestInputResolution.Should().BeEquivalentTo(
            state.LatestInputResolution);
        agent.State.RecentInputResolutions.Should().ContainSingle(result =>
            result.RequestId == "input-resolved-alpha");
        agent.State.LatestApprovalResolution.Should().BeEquivalentTo(
            state.LatestApprovalResolution);
        agent.State.RecentApprovalResolutions.Should().ContainSingle(result =>
            result.RequestId == "approval-resolved-alpha");
    }

    [Fact]
    public async Task LlmToolResult_ShouldCommitSuccessorRequestedWaterlineBeforeDispatch()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        NyxIdChatConversationGAgent? agent = null;
        NyxIdChatConversationGAgentState? stateObservedAtSuccessorDispatch = null;
        IReadOnlyList<StateEvent>? eventsObservedAtSuccessorDispatch = null;
        var dispatchCount = 0;
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            async (_, envelope) =>
            {
                dispatchCount++;
                if (dispatchCount != 2)
                    return;

                var command = envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>();
                command.InputCase.Should().Be(
                    NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
                stateObservedAtSuccessorDispatch = agent!.State.Clone();
                eventsObservedAtSuccessorDispatch = await eventStore.GetEventsAsync(
                    conversationActorId);
            });
        using var services = BuildEventSourcingServices(eventStore);
        agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var llmResult = new NyxIdChatOperationResultSignal
        {
            Key = llmKey,
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-alpha",
                        ToolName = "repository_update",
                        ArgumentsJson = "{\"repositoryId\":\"repo-alpha\"}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            SideEffectKind = "repository.update",
                            MayChangeExternalState = true,
                        },
                    },
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, llmResult));

        dispatch.Calls.Should().HaveCount(2);
        stateObservedAtSuccessorDispatch.Should().NotBeNull();
        var toolStep = stateObservedAtSuccessorDispatch!.ActiveTask.Steps.Last();
        toolStep.Kind.Should().Be(NyxIdChatStepKind.Tool);
        toolStep.Status.Should().Be(NyxIdChatStepStatus.Running);
        toolStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        eventsObservedAtSuccessorDispatch.Should().NotBeNull();
        eventsObservedAtSuccessorDispatch![^1].EventData.Is(
            NyxIdChatOperationReconciledEvent.Descriptor).Should().BeTrue();
        var committedReconciliation = eventsObservedAtSuccessorDispatch[^1].EventData
            .Unpack<NyxIdChatOperationReconciledEvent>();
        committedReconciliation.Result.Llm.ToolCalls.Should().ContainSingle()
            .Which.ArgumentsJson.Should().BeEmpty(
                "tool arguments are transient dispatch input, not durable product facts");
        dispatch.Calls[^1].Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>()
            .Tool.ArgumentsJson.Should().Be("{\"repositoryId\":\"repo-alpha\"}");
        eventsObservedAtSuccessorDispatch.Should().HaveCount(afterStart.Count + 1,
            "the successor dispatched waterline is committed only after dispatch admission");

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(afterStart.Count + 2);
        committed[^1].EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor).Should().BeTrue();
        agent.State.ActiveTask.Steps.Last().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);
    }

    [Fact]
    public async Task ChildProgress_ShouldCommitMatchingMonotonicSequenceAndIgnoreDuplicateOrWrongKey()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var progress = new NyxIdChatOperationProgressSignal
        {
            Key = key,
            Sequence = 1,
            Text = new NyxIdChatTextProgress { Delta = "hello" },
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, progress));

        var afterFirst = await eventStore.GetEventsAsync(conversationActorId);
        afterFirst.Should().HaveCount(afterStart.Count + 1);
        afterFirst[^1].EventData.TypeUrl.Should().EndWith("NyxIdChatOperationProgressedEvent");
        agent.State.ProgressSequence.Should().Be(2);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, progress.Clone()));
        var wrong = progress.Clone();
        wrong.Sequence = 2;
        wrong.Key.StepId = "step-wrong";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, wrong));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(afterFirst.Count);
        agent.State.ProgressSequence.Should().Be(2);
    }

    [Fact]
    public async Task StopDuringDispatchedLlm_ShouldCommitDurableFenceAndStoppedTerminal()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatStopCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = conversationActorId,
            TurnId = "turn-alpha",
            StopRequestId = "stop-alpha",
            ClientRequestId = "client-stop-alpha",
            CommandId = "command-stop-alpha",
            CorrelationId = "correlation-stop-alpha",
            ExpectedStateVersion = afterStart[^1].Version,
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(afterStart.Count + 1);
        committed[^1].EventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor).Should().BeTrue();
        var fence = committed[^1].EventData.Unpack<NyxIdChatControlFenceCommittedEvent>().Fence;
        fence.Kind.Should().Be(NyxIdChatControlKind.Stop);
        fence.Outcome.Should().Be(NyxIdChatControlOutcome.Uncancellable);
        fence.RequestId.Should().Be("stop-alpha");
        agent.State.ControlFence.Should().BeEquivalentTo(fence);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        var stoppedStep = agent.State.ActiveTask.Steps.Should().ContainSingle().Which;
        stoppedStep.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        stoppedStep.Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched,
            "an unsupported physical cancellation must not pretend the child operation ended");
        dispatch.OperationCalls.Should().ContainSingle(
            "stop commits a fence and does not dispatch more provider work");
    }

    [Fact]
    public async Task LateToolReceiptAfterStop_ShouldOnlyRefineExactEffectEvidence()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));

        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = llmKey,
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-alpha",
                        ToolName = "repository_update",
                        ArgumentsJson = "{\"repositoryId\":\"repo-alpha\"}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            SideEffectKind = "repository.update",
                            MayChangeExternalState = true,
                        },
                    },
                },
            },
        }));
        var toolKey = agent.State.ActiveTask.Steps[^1].Operation.Key.Clone();
        var beforeStop = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateStopCommand(beforeStop[^1].Version)));

        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        agent.State.ActiveTask.Steps[^1].Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        agent.State.ActiveTask.Steps[^1].ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = toolKey,
            Tool = new NyxIdChatToolOperationResult
            {
                ResultJson = "{\"secret\":\"must-not-be-committed\"}",
                ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
                Receipt = new Aevatar.AI.Abstractions.AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    Status = Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success,
                    SubjectKind = "repository",
                    SubjectId = "repo-alpha",
                },
            },
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(beforeStop.Count + 2);
        committed[^1].EventData.TypeUrl.Should().EndWith(
            "NyxIdChatLateOperationEvidenceCommittedEvent");
        var evidence = committed[^1].EventData.Unpack<NyxIdChatLateOperationEvidenceCommittedEvent>();
        evidence.Key.Should().BeEquivalentTo(toolKey);
        evidence.OperationPhase.Should().Be(NyxIdChatOperationPhase.Succeeded);
        evidence.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        evidence.ToolReceipt.Status.Should().Be(
            Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success);
        evidence.ToString().Should().NotContain("must-not-be-committed");

        var stoppedTool = agent.State.ActiveTask.Steps[^1];
        stoppedTool.Status.Should().Be(NyxIdChatStepStatus.Uncertain,
            "late evidence cannot regress or advance the terminal stopped step");
        stoppedTool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        stoppedTool.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        dispatch.OperationCalls.Should().HaveCount(2,
            "late evidence cannot start an old-plan successor");

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = toolKey,
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                Receipt = new Aevatar.AI.Abstractions.AgentToolReceipt
                {
                    CallId = "call-alpha",
                    ToolName = "repository_update",
                    Status = Aevatar.AI.Abstractions.AgentToolReceiptStatus.Error,
                },
            },
        }));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(committed.Count,
            "terminal exact evidence is monotonic and conflicting duplicates fail closed");
        agent.State.ActiveTask.Steps[^1].ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.Confirmed);
    }

    [Fact]
    public async Task LateLlmOutputAfterStop_ShouldBeDiscardedWithoutSuccessor()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateStopCommand(afterStart[^1].Version)));

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = key,
            Llm = new NyxIdChatLLMOperationResult
            {
                Content = "late body must disappear",
                ReasoningContent = "late reasoning must disappear",
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "late-call",
                        ToolName = "late_tool",
                        ArgumentsJson = "{\"secret\":\"late\"}",
                        Safety = new NyxIdChatToolCallSafety { IsReadOnly = true },
                    },
                },
            },
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(afterStart.Count + 1);
        committed.Select(static evt => evt.EventData.ToString()).Should().NotContain(value =>
            value.Contains("late body", StringComparison.Ordinal) ||
            value.Contains("late reasoning", StringComparison.Ordinal) ||
            value.Contains("late-call", StringComparison.Ordinal));
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        dispatch.OperationCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task StopReplay_ShouldUseDurableFenceAfterLaterControlResult()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var stop = CreateStopCommand(afterStart[^1].Version);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, stop));

        var conflicting = stop.Clone();
        conflicting.ClientRequestId = "client-stop-conflict";
        conflicting.CommandId = "command-stop-conflict";
        conflicting.CorrelationId = "correlation-stop-conflict";
        conflicting.ExpectedStateVersion = 0;
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, conflicting));

        var afterConflict = await eventStore.GetEventsAsync(conversationActorId);
        afterConflict.Should().HaveCount(afterStart.Count + 2);
        var rejected = afterConflict[^1].EventData
            .Unpack<NyxIdChatControlFenceCommittedEvent>();
        rejected.Fence.Outcome.Should().Be(NyxIdChatControlOutcome.Rejected);
        rejected.Fence.ReasonCode.Should().Be(NyxIdChatControlCommands.ControlConflict);
        agent.State.ControlFence.RequestId.Should().Be("stop-alpha");
        agent.State.LatestControlResult.Outcome.Should().Be(NyxIdChatControlOutcome.Rejected);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, stop.Clone()));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(afterConflict.Count,
            "the accepted durable fence owns exact stop replay even when latest_control_result changed");
        agent.State.ControlFence.RequestId.Should().Be("stop-alpha");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
    }

    [Fact]
    public async Task OrdinaryStartDuringActiveTurn_ShouldCommitTypedSteeringRequiredRejection()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var concurrent = CreateStartTurnCommand();
        concurrent.TurnId = "turn-beta";
        concurrent.TaskId = "task-beta";
        concurrent.ClientRequestId = "client-beta";
        concurrent.CommandId = "command-beta";
        concurrent.CorrelationId = "correlation-beta";
        concurrent.Prompt = "change direction";

        var act = () => agent.HandleEventAsync(CreateEnvelope(conversationActorId, concurrent));

        await act.Should().NotThrowAsync();
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(afterStart.Count + 1);
        committed[^1].EventData.TypeUrl.Should().EndWith("NyxIdChatTurnAdmissionRejectedEvent");
        agent.State.ActiveTurn.TurnId.Should().Be("turn-alpha");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
    }

    [Fact]
    public async Task TerminalTurn_ShouldAllowASeparateOrdinaryTurn()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var completedKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = completedKey,
            Llm = new NyxIdChatLLMOperationResult { Content = "done" },
        }));
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        var pendingTerminal = agent.State.PendingHistoryTerminal.Clone();

        var next = CreateStartTurnCommand();
        next.TurnId = "turn-beta";
        next.TaskId = "task-beta";
        next.ClientRequestId = "client-beta";
        next.CommandId = "command-beta";
        next.CorrelationId = "correlation-beta";
        next.Prompt = "next request";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, next));

        agent.State.ActiveTurn.TurnId.Should().Be("turn-beta");
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.ActiveTask.TaskId.Should().Be("task-beta");
        agent.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == "turn-alpha" &&
            summary.Status == NyxIdChatTurnStatus.Succeeded);
        agent.State.PendingHistoryTerminal.ToByteString().Should().Equal(
            pendingTerminal.ToByteString(),
            "starting another turn cannot discard an undelivered terminal fact");
        dispatch.OperationCalls.Should().HaveCount(2);

        var betaKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var beforeSecondTerminal = await eventStore.GetEventsAsync(conversationActorId);
        var secondTerminal = () => agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = betaKey,
                Llm = new NyxIdChatLLMOperationResult { Content = "second answer" },
            }));

        await secondTerminal.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different*history terminal delivery*pending*");
        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            beforeSecondTerminal.Count,
            "a second terminal cannot overwrite the single durable outbox");
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.PendingHistoryTerminal.ToByteString().Should().Equal(
            pendingTerminal.ToByteString());
    }

    [Fact]
    public async Task TerminalTurn_ReusedIdentityWithDifferentPrompt_ShouldRejectIdempotencyConflict()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        var original = CreateStartTurnCommand();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, original));
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult { Content = "done" },
            }));
        var beforeConflict = await eventStore.GetEventsAsync(conversationActorId);

        var conflicting = original.Clone();
        conflicting.Prompt = "different request";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, conflicting));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(beforeConflict.Count + 1);
        var rejection = committed[^1].EventData
            .Unpack<NyxIdChatTurnAdmissionRejectedEvent>();
        rejection.RequestedTurnId.Should().Be(original.TurnId);
        rejection.ReasonCode.Should().Be("IDEMPOTENCY_CONFLICT");
        dispatch.OperationCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task SteeringDuringDispatchedOperation_ShouldFenceOldTurnAndAllocateOneContinuation()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);

        var steering = new NyxIdChatSteeringCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = conversationActorId,
            TurnId = "turn-alpha",
            SteeringId = "steering-alpha",
            ClientRequestId = "client-steering-alpha",
            CommandId = "command-steering-alpha",
            CorrelationId = "correlation-steering-alpha",
            Instruction = "Use the safer read-only approach.",
            ExpectedStateVersion = afterStart[^1].Version,
        };
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Count(evt => evt.EventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor))
            .Should().Be(1);
        committed.Count(evt => evt.EventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
            .Should().Be(1);
        var committedFence = committed.Single(evt =>
                evt.EventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor))
            .EventData.Unpack<NyxIdChatControlFenceCommittedEvent>();
        committedFence.State.ControlFence.Should().NotBeNull(
            "the control commit must carry the durable execution fence");
        var committedAdmission = committed.Single(evt =>
                evt.EventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
            .EventData.Unpack<NyxIdChatContinuationAdmissionCommittedEvent>();
        committedAdmission.State.ControlFence.Should().NotBeNull(
            "the continuation snapshot must preserve the old-plan fence");
        committedFence.State.PendingHistoryTerminal.Should().NotBeNull();
        committedAdmission.State.PendingHistoryTerminal.ToByteString().Should().Equal(
            committedFence.State.PendingHistoryTerminal.ToByteString(),
            "the second steering snapshot cannot discard the first commit's terminal outbox");
        agent.State.ControlFence.Kind.Should().Be(NyxIdChatControlKind.Steering);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        agent.State.ContinuationAdmission.RequestId.Should().Be("steering-alpha");
        agent.State.ContinuationAdmission.OriginTurnId.Should().Be("turn-alpha");
        agent.State.ContinuationAdmission.ContinuationTurnId.Should().NotBeNullOrWhiteSpace();
        agent.State.ContinuationAdmission.ContinuationTurnId.Should().NotBe("turn-alpha");
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);
        dispatch.OperationCalls.Should().ContainSingle(
            "the revised instruction cannot run concurrently with an uncancellable old operation");
    }

    [Fact]
    public async Task SteeringReplayAfterLateLlmCheckpoint_ShouldStartOneContinuationWithTransientCapability()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var oldKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var steering = CreateSteeringCommand(afterStart[^1].Version);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));

        var continuationTurnId = agent.State.ContinuationAdmission.ContinuationTurnId;
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);
        dispatch.OperationCalls.Should().ContainSingle();

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = oldKey,
            Llm = new NyxIdChatLLMOperationResult
            {
                Content = "discard this old-plan answer",
                ReasoningContent = "discard this old-plan reasoning",
            },
        }));

        var checkpointEvents = await eventStore.GetEventsAsync(conversationActorId);
        checkpointEvents.Should().HaveCount(afterStart.Count + 3);
        checkpointEvents[^1].EventData.Is(
            NyxIdChatLateOperationEvidenceCommittedEvent.Descriptor).Should().BeTrue();
        checkpointEvents[^1].EventData.ToString().Should()
            .NotContain("discard this old-plan answer")
            .And.NotContain("discard this old-plan reasoning");
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        dispatch.OperationCalls.Should().ContainSingle(
            "the late old-plan result only establishes a checkpoint");

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        agent.State.ActiveTurn.TurnId.Should().Be("turn-alpha",
            "the continuation must not advance inline in the steering actor turn");
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);
        dispatch.StartTurnCalls.Should().ContainSingle();
        var selfDispatch = dispatch.StartTurnCalls.Single();
        selfDispatch.ActorId.Should().Be(conversationActorId);
        selfDispatch.Envelope.Route.Direct.TargetActorId.Should().Be(conversationActorId);
        selfDispatch.Envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor).Should().BeTrue();
        var continuationStart = selfDispatch.Envelope.Payload
            .Unpack<NyxIdChatStartTurnCommand>();
        continuationStart.TurnId.Should().Be(continuationTurnId);
        continuationStart.CommandId.Should().Be(selfDispatch.Envelope.Id);
        continuationStart.CommandId.Should().NotBe(steering.CommandId,
            "the self continuation has its own stable inbox identity");
        continuationStart.LlmControl.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");

        await agent.HandleEventAsync(selfDispatch.Envelope);

        agent.State.ActiveTurn.TurnId.Should().Be(continuationTurnId);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        dispatch.OperationCalls.Should().HaveCount(2);
        var continuation = dispatch.OperationCalls.Last().Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        continuation.Key.TurnId.Should().Be(continuationTurnId);
        continuation.Llm.Request.Prompt.Should().Be("Use the safer read-only approach.");
        continuation.Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        continuation.Llm.Request.ToolContext.Credentials.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        dispatch.OperationCalls.Should().HaveCount(2,
            "a started continuation is idempotent under exact steering replay");
    }

    [Fact]
    public async Task SteeringAcceptedCheckpointReplay_ShouldRedispatchSameSelfContinuationAfterDeliveryGap()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var initial = CreateController(services, conversationActorId);
        await initial.ActivateAsync();
        await initial.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var requestedCheckpoint = initial.State.Clone();
        requestedCheckpoint.ActiveTask.Steps.Single().Operation.Phase =
            NyxIdChatOperationPhase.Requested;
        await PersistTestStateAsync(
            eventStore,
            conversationActorId,
            version: afterStart[^1].Version + 1,
            requestedCheckpoint);
        var checkpointVersion = afterStart[^1].Version + 1;

        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var reactivated = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(reactivated, conversationActorId);
        await reactivated.ActivateAsync();
        var steering = CreateSteeringCommand(expectedStateVersion: checkpointVersion);

        await reactivated.HandleEventAsync(CreateEnvelope(conversationActorId, steering));

        reactivated.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        dispatch.RecoveryCalls.Should().ContainSingle(
            "activation queues typed recovery for the requested LLM waterline");
        dispatch.StartTurnCalls.Should().ContainSingle(
            "steering queues one continuation");
        var activationRecovery = dispatch.Calls.Single(call =>
            call.Envelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor)).Envelope.Clone();
        var firstSelfMessage = dispatch.Calls.Single(call =>
            call.Envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor)).Envelope.Clone();
        var beforeReplay = await eventStore.GetEventsAsync(conversationActorId);

        await reactivated.HandleEventAsync(activationRecovery);

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            beforeReplay.Count,
            "the steering commits advance the version and make the earlier activation recovery stale");
        dispatch.RecoveryCalls.Should().ContainSingle(
            "stale recovery cannot replay the old LLM or create a turn actor");
        dispatch.StartTurnCalls.Should().ContainSingle();

        await reactivated.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            beforeReplay.Count,
            "an exact replay must not commit the admission twice");
        dispatch.StartTurnCalls.Should().HaveCount(2,
            "an accepted but unhandled self continuation must be safely redeliverable");
        dispatch.StartTurnCalls[^1].Envelope.Id.Should().Be(firstSelfMessage.Id);
        dispatch.StartTurnCalls[^1].Envelope.Payload.ToByteString().Should().Equal(
            firstSelfMessage.Payload.ToByteString());
        reactivated.State.ToString().Should().NotContain("steering-runtime-token-alpha");
    }

    [Fact]
    public async Task SteeringSameIdentityWithDifferentInstruction_ShouldFailClosed()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var steering = CreateSteeringCommand(afterStart[^1].Version);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));
        var continuationTurnId = agent.State.ContinuationAdmission.ContinuationTurnId;

        var conflicting = steering.Clone();
        conflicting.Instruction = "Delete everything instead.";
        conflicting.ExpectedStateVersion = 0;
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, conflicting));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var conflict = committed[^1].EventData.Unpack<NyxIdChatControlFenceCommittedEvent>();
        conflict.Fence.Outcome.Should().Be(NyxIdChatControlOutcome.Rejected);
        conflict.Fence.ReasonCode.Should().Be(NyxIdChatControlCommands.ControlConflict);
        agent.State.ContinuationAdmission.ContinuationTurnId.Should().Be(continuationTurnId);
        agent.State.ContinuationAdmission.Instruction.Should().Be(
            "Use the safer read-only approach.");
    }

    [Fact]
    public async Task RetryFailedLlm_ShouldCommitGenerationBeforeTransientDispatch()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        IReadOnlyList<StateEvent>? eventsObservedAtRetryDispatch = null;
        NyxIdChatConversationGAgent? agent = null;
        var dispatchCount = 0;
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            async (_, envelope) =>
            {
                dispatchCount++;
                if (dispatchCount != 2)
                    return;

                eventsObservedAtRetryDispatch = await eventStore.GetEventsAsync(
                    conversationActorId);
                var retry = envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>();
                retry.Key.OperationGeneration.Should().Be(2);
                retry.Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(
                    "retry-runtime-token-alpha");
                agent!.State.ActiveTask.Steps.Single().Operation.Key
                    .Should().BeEquivalentTo(retry.Key);
            });
        using var services = BuildEventSourcingServices(eventStore);
        agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var firstKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = firstKey,
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "MODEL_FAILED",
                SafeMessage = "The model attempt failed.",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        }));
        agent.State.ActiveTask.Steps.Single().AvailableActions.Retry.Should().BeTrue();

        var beforeRetry = await eventStore.GetEventsAsync(conversationActorId);
        var retry = CreateRetryCommand(
            firstKey.StepId,
            expectedStateVersion: beforeRetry[^1].Version);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, retry));

        eventsObservedAtRetryDispatch.Should().NotBeNull();
        eventsObservedAtRetryDispatch![^1].EventData.Is(
            NyxIdChatStepControlCommittedEvent.Descriptor).Should().BeTrue();
        var committed = eventsObservedAtRetryDispatch[^1].EventData
            .Unpack<NyxIdChatStepControlCommittedEvent>();
        committed.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        committed.Result.OperationGeneration.Should().Be(2);
        committed.State.ToString().Should().NotContain("retry-runtime-token-alpha");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.RecentTerminalTurns.Should().BeEmpty();

        var all = await eventStore.GetEventsAsync(conversationActorId);
        all[^1].EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor).Should().BeTrue();
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, retry.Clone()));
        dispatch.Calls.Should().HaveCount(2,
            "an already dispatched retry is an idempotent replay");
        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(all.Count);
    }

    [Fact]
    public async Task LaterTurn_ShouldPreserveBoundedStepControlReplayFacts()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var firstKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = firstKey,
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "MODEL_FAILED",
                SafeMessage = "The model attempt failed.",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        }));
        var beforeRetry = await eventStore.GetEventsAsync(conversationActorId);
        var retry = CreateRetryCommand(
            firstKey.StepId,
            expectedStateVersion: beforeRetry[^1].Version);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, retry));
        var retryKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = retryKey,
            Llm = new NyxIdChatLLMOperationResult { Content = "Recovered." },
        }));
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);

        var nextTurn = CreateStartTurnCommand();
        nextTurn.TurnId = "turn-beta";
        nextTurn.TaskId = "task-beta";
        nextTurn.ClientRequestId = "client-beta";
        nextTurn.CommandId = "command-beta";
        nextTurn.CorrelationId = "correlation-beta";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, nextTurn));

        agent.State.ActiveTurn.TurnId.Should().Be("turn-beta");
        agent.State.RecentStepControlResults.Should().ContainSingle(result =>
            result.RequestId == "retry-alpha" &&
            result.TurnId == "turn-alpha" &&
            result.Outcome == NyxIdChatTransitionOutcome.Accepted);
        agent.State.LatestStepControlResult.RequestId.Should().Be("retry-alpha");

        var beforeReplay = await eventStore.GetEventsAsync(conversationActorId);
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, retry.Clone()));
        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            beforeReplay.Count,
            "an old exact request identity remains an idempotent replay after a later turn starts");
    }

    [Fact]
    public async Task SkipOptionalFailedStep_ShouldCommitTypedSuccessWithoutDispatch()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, CreateStartTurnCommand()));
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = key,
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "OPTIONAL_FAILED",
                SafeMessage = "The optional step failed.",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        }));
        var failed = agent.State.Clone();
        failed.ActiveTask.Steps.Single().Required = false;
        failed.ActiveTask.Steps.Single().AvailableActions =
            NyxIdChatTaskTransitionPolicy.ResolveAvailableActions(
                failed.ActiveTask.Steps.Single());
        var beforeReplacement = await eventStore.GetEventsAsync(conversationActorId);
        var replacementVersion = beforeReplacement[^1].Version + 1;
        await PersistTestStateAsync(
            eventStore,
            conversationActorId,
            version: replacementVersion,
            failed);
        var reactivated = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(reactivated, conversationActorId);
        await reactivated.ActivateAsync();

        await reactivated.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatSkipStepCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = conversationActorId,
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = key.StepId,
            SkipRequestId = "skip-alpha",
            ClientRequestId = "client-skip-alpha",
            CommandId = "command-skip-alpha",
            CorrelationId = "correlation-skip-alpha",
            ExpectedOperationGeneration = 1,
            ExpectedStateVersion = replacementVersion,
        }));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed[^1].EventData.Is(NyxIdChatStepControlCommittedEvent.Descriptor).Should().BeTrue();
        reactivated.State.ActiveTask.Steps.Single().Status.Should().Be(
            NyxIdChatStepStatus.Skipped);
        reactivated.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        reactivated.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        dispatch.OperationCalls.Should().ContainSingle("skip never starts provider or tool I/O");
    }

    [Fact]
    public void StreamingCommand_ShouldKeepClientRequestIdentityDistinctFromTurnIdentity()
    {
        typeof(NyxIdChatCommand).GetProperty("ClientRequestId").Should().NotBeNull();
    }

    [Fact]
    public void StreamingEnvelope_WithoutCredentialClassification_ShouldKeepCredentialSourceUnreadable()
    {
        var factory = new NyxIdChatCommandEnvelopeFactory();
        var command = new NyxIdChatCommand(
            "conversation-alpha",
            "scope-alpha",
            "hello",
            "turn-alpha",
            "runtime-token-alpha",
            null,
            null);

        var envelope = factory.CreateEnvelope(
            command,
            new CommandContext(
                "conversation-alpha",
                "command-alpha",
                "correlation-alpha",
                new Dictionary<string, string>()));

        envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor).Should().BeTrue();
        var start = envelope.Payload.Unpack<NyxIdChatStartTurnCommand>();
        start.ScopeId.Should().Be("scope-alpha");
        start.ConversationActorId.Should().Be("conversation-alpha");
        start.TurnId.Should().Be("turn-alpha");
        start.TaskId.Should().NotBeNullOrWhiteSpace();
        start.TaskId.Should().NotBe(start.TurnId);
        start.CommandId.Should().Be("command-alpha");
        start.CorrelationId.Should().Be("correlation-alpha");
        start.Prompt.Should().Be("hello");
        start.LlmControl.NyxIdAccessToken.Should().Be("runtime-token-alpha");
        start.ToolContext.Credentials.NyxIdAccessToken.Should().Be("runtime-token-alpha");
        AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                AgentToolExecutionContextMapper.FromPayload(start.ToolContext).Credentials)
            .Should().BeNull();
    }

    [Fact]
    public void FirstTextEnvelope_ShouldDispatchTypedCreateAndStartCommand()
    {
        var factory = new NyxIdChatCommandEnvelopeFactory();
        var command = new NyxIdChatCommand(
            "conversation-alpha",
            "scope-alpha",
            "hello",
            "turn-alpha",
            "runtime-token-alpha",
            null,
            null,
            ClientRequestId: "client-alpha",
            CreateIfMissing: true)
        {
            CreatedLocally = true,
        };

        var envelope = factory.CreateEnvelope(
            command,
            new CommandContext(
                "conversation-alpha",
                "command-alpha",
                "correlation-alpha",
                new Dictionary<string, string>()));

        envelope.Payload.Is(NyxIdChatConversationCreateCommand.Descriptor).Should().BeTrue();
        var create = envelope.Payload.Unpack<NyxIdChatConversationCreateCommand>();
        create.ScopeId.Should().Be("scope-alpha");
        create.CreatedLocally.Should().BeTrue();
        create.FirstTurn.ConversationActorId.Should().Be("conversation-alpha");
        create.FirstTurn.TurnId.Should().Be("turn-alpha");
        create.FirstTurn.ClientRequestId.Should().Be("client-alpha");
    }

    [Fact]
    public void TaskContracts_ShouldDeclareAtomicControllerStartAndTurnActorWaterlines()
    {
        var messageNames = NyxidChatTaskReflection.Descriptor.MessageTypes
            .Select(static descriptor => descriptor.Name)
            .ToArray();

        messageNames.Should().Contain(
        [
            "NyxIdChatTurnStartedEvent",
            "NyxIdChatTurnGAgentState",
            "NyxIdChatTurnOperationAdmittedEvent",
            "NyxIdChatTurnOperationCompletedEvent",
            "NyxIdChatTurnOperationDeliveredEvent",
        ]);
    }

    [Fact]
    public async Task AskUserToolCall_ShouldMaterializePendingInputAndResumeAfterReload()
    {
        const string conversationActorId = "conversation-alpha";
        const string refreshedToken = "refreshed-token-sentinel";
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var initial = CreateController(services, conversationActorId, dispatch);
        await initial.ActivateAsync();
        await initial.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateStartTurnCommand()));
        var llmKey = initial.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await initial.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = llmKey,
                Llm = new NyxIdChatLLMOperationResult
                {
                    ToolCalls =
                    {
                        new NyxIdChatToolCall
                        {
                            CallId = "call-ask-user-alpha",
                            ToolName = "ask_user",
                            ArgumentsJson = """
                                {
                                  "question": "Choose deployment regions.",
                                  "options": [
                                    {"label": "Singapore", "description": "Asia region"},
                                    {"label": "Frankfurt", "description": "Europe region"}
                                  ],
                                  "multi_select": true
                                }
                                """,
                            Safety = new NyxIdChatToolCallSafety
                            {
                                IsReadOnly = true,
                                MayChangeExternalState = false,
                            },
                        },
                    },
                },
            }));

        initial.State.PendingInput.Should().BeNull();
        initial.State.PendingInputRequest.Should().NotBeNull();
        var selfRequest = dispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(NyxIdChatInputRequestCommand.Descriptor))
            .Which.Envelope.Clone();
        await initial.HandleEventAsync(selfRequest);

        initial.State.PendingInput.Should().NotBeNull();
        var pending = initial.State.PendingInput!;
        pending.ToolCallId.Should().Be("call-ask-user-alpha");
        pending.MultiSelect.Should().BeTrue();
        pending.Options.Should().HaveCount(2);
        pending.Options.Should().OnlyContain(static option =>
            option.OptionId.StartsWith("option-", StringComparison.Ordinal));
        initial.State.PendingInputRequest.Should().BeNull();

        var recoveryDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var recovered = CreateController(services, conversationActorId, recoveryDispatch);
        await recovered.ActivateAsync();
        recovered.State.PendingInput.Should().BeEquivalentTo(pending);
        recoveryDispatch.Calls.Should().BeEmpty(
            "a committed pending input must not rematerialize the outbox self-message");

        var answer = new NyxIdChatInputAnswer
        {
            Selection = new NyxIdChatInputSelectionAnswer(),
        };
        answer.Selection.OptionIds.AddRange(pending.Options.Select(static option => option.OptionId));
        var committedBeforeResolution = await eventStore.GetEventsAsync(conversationActorId);
        await recovered.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatInputResolveCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = conversationActorId,
                RequestId = pending.RequestId,
                ClientRequestId = "client-input-alpha",
                Answer = answer,
                ExpectedStateVersion = committedBeforeResolution.Count,
                CommandId = "command-input-alpha",
                CorrelationId = "correlation-input-alpha",
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Credentials = new AgentToolCredentialsPayload
                    {
                        NyxIdAccessToken = refreshedToken,
                    },
                },
            }));

        recovered.State.PendingInput.Should().BeNull();
        recovered.State.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Input)
            .Status.Should().Be(NyxIdChatStepStatus.Done);
        recovered.State.ActiveTask.Steps.Last().Kind.Should().Be(NyxIdChatStepKind.Llm);
        recovered.State.ActiveTask.Steps.Last().Status.Should().Be(NyxIdChatStepStatus.Running);
        var continuation = recoveryDispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        continuation.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation);
        continuation.InputContinuation.Answer.Selection.OptionIds.Should()
            .Equal(answer.Selection.OptionIds);
        continuation.InputContinuation.ToolContext.Credentials.NyxIdAccessToken.Should().Be(refreshedToken);

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var resolution = committed.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatInputResolutionCommittedEvent.Descriptor))
            .Which.EventData.Unpack<NyxIdChatInputResolutionCommittedEvent>();
        resolution.Resolution.AnswerSha256.Should().NotBeEmpty();
        resolution.ToString().Should().NotContain(refreshedToken);
        committed.Should().OnlyContain(item =>
            !item.EventData.ToString().Contains(refreshedToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InputResolution_WhenContinuationDispatchFails_ShouldCommitSafeTerminal()
    {
        const string conversationActorId = "conversation-alpha";
        const string refreshedToken = "dispatch-failure-token-sentinel";
        var failInputContinuation = false;
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort(
            [],
            (_, envelope) =>
            {
                if (failInputContinuation &&
                    envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor) &&
                    envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>().InputCase ==
                    NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation)
                {
                    throw new InvalidOperationException("dispatch failed with bearer-secret");
                }

                return Task.CompletedTask;
            });
        using var services = BuildEventSourcingServices(eventStore);
        var controller = CreateController(services, conversationActorId, dispatch);
        await controller.ActivateAsync();
        await controller.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateStartTurnCommand()));
        var llmKey = controller.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await controller.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = llmKey,
                Llm = new NyxIdChatLLMOperationResult
                {
                    ToolCalls =
                    {
                        new NyxIdChatToolCall
                        {
                            CallId = "call-ask-user-alpha",
                            ToolName = "ask_user",
                            ArgumentsJson = """
                                {
                                  "question": "Choose a deployment region.",
                                  "options": [
                                    {"label": "Singapore"},
                                    {"label": "Frankfurt"}
                                  ]
                                }
                                """,
                            Safety = new NyxIdChatToolCallSafety
                            {
                                IsReadOnly = true,
                                MayChangeExternalState = false,
                            },
                        },
                    },
                },
            }));
        var selfRequest = dispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(NyxIdChatInputRequestCommand.Descriptor))
            .Which.Envelope.Clone();
        await controller.HandleEventAsync(selfRequest);

        var pending = controller.State.PendingInput!;
        var answer = new NyxIdChatInputAnswer
        {
            Selection = new NyxIdChatInputSelectionAnswer(),
        };
        answer.Selection.OptionIds.Add(pending.Options[0].OptionId);
        var committedBeforeResolution = await eventStore.GetEventsAsync(conversationActorId);
        failInputContinuation = true;

        await controller.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatInputResolveCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = conversationActorId,
                RequestId = pending.RequestId,
                ClientRequestId = "client-input-dispatch-failure",
                Answer = answer,
                ExpectedStateVersion = committedBeforeResolution.Count,
                CommandId = "command-input-dispatch-failure",
                CorrelationId = "correlation-input-dispatch-failure",
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Credentials = new AgentToolCredentialsPayload
                    {
                        NyxIdAccessToken = refreshedToken,
                    },
                },
            }));

        controller.State.PendingInput.Should().BeNull();
        controller.State.LatestInputResolution.Outcome.Should().Be(
            NyxIdChatNeedsYouResolutionOutcome.Accepted);
        controller.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        controller.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        controller.State.ActiveTask.FailureCode.Should().Be("NYXID_CHAT_OPERATION_DISPATCH_FAILED");
        controller.State.ActiveTask.Steps.Should().OnlyContain(step =>
            step.Status != NyxIdChatStepStatus.Waiting &&
            step.Status != NyxIdChatStepStatus.Running);
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().Contain(item =>
            item.EventData.Is(NyxIdChatInputResolutionCommittedEvent.Descriptor));
        committed.Should().Contain(item =>
            item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor));
        committed.Should().OnlyContain(item =>
            !item.EventData.ToString().Contains(refreshedToken, StringComparison.Ordinal));
    }

    private static NyxIdChatStartTurnCommand CreateStartTurnCommand() => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        ClientRequestId = "client-alpha",
        CommandId = "command-alpha",
        CorrelationId = "correlation-alpha",
        Prompt = "hello",
        LlmControl = new Aevatar.AI.Abstractions.LLMControlContextPayload
        {
            NyxIdAccessToken = "runtime-token-alpha",
        },
        ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
        {
            Credentials = new Aevatar.AI.Abstractions.AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "runtime-token-alpha",
            },
        },
    };

    private static void SetOwner(NyxIdChatStartTurnCommand command, string ownerSubject)
    {
        command.ToolContext ??= new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload();
        command.ToolContext.Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
        {
            OwnerSubject = ownerSubject,
        };
    }

    private static NyxIdChatStartTurnCommand WithOwner(
        NyxIdChatStartTurnCommand command,
        string ownerSubject)
    {
        SetOwner(command, ownerSubject);
        return command;
    }

    private static NyxIdChatStopCommand CreateStopCommand(long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        StopRequestId = "stop-alpha",
        ClientRequestId = "client-stop-alpha",
        CommandId = "command-stop-alpha",
        CorrelationId = "correlation-stop-alpha",
        ExpectedStateVersion = expectedStateVersion,
    };

    private static NyxIdChatSteeringCommand CreateSteeringCommand(long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        SteeringId = "steering-alpha",
        ClientRequestId = "client-steering-alpha",
        CommandId = "command-steering-alpha",
        CorrelationId = "correlation-steering-alpha",
        Instruction = "Use the safer read-only approach.",
        ExpectedStateVersion = expectedStateVersion,
        LlmControl = new Aevatar.AI.Abstractions.LLMControlContextPayload
        {
            NyxIdAccessToken = "steering-runtime-token-alpha",
        },
        ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
        {
            Credentials = new Aevatar.AI.Abstractions.AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "steering-runtime-token-alpha",
            },
        },
    };

    private static NyxIdChatRetryStepCommand CreateRetryCommand(
        string stepId,
        long expectedStateVersion) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = stepId,
        RetryRequestId = "retry-alpha",
        ClientRequestId = "client-retry-alpha",
        CommandId = "command-retry-alpha",
        CorrelationId = "correlation-retry-alpha",
        ExpectedOperationGeneration = 1,
        ExpectedStateVersion = expectedStateVersion,
        LlmControl = new Aevatar.AI.Abstractions.LLMControlContextPayload
        {
            NyxIdAccessToken = "retry-runtime-token-alpha",
        },
        ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
        {
            Credentials = new Aevatar.AI.Abstractions.AgentToolCredentialsPayload
            {
                NyxIdAccessToken = "retry-runtime-token-alpha",
            },
        },
    };

    private static NyxIdChatActionContinueCommand CreateActionContinueCommand(
        string actionRequestId)
    {
        var command = new NyxIdChatActionContinueCommand
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            OriginTurnId = "turn-alpha",
            ContinuationTurnId = "turn-action-alpha",
            OwnerSubject = "owner-alpha",
            ClientRequestId = "client-action-alpha",
            CommandId = "command-action-alpha",
            CorrelationId = "correlation-action-alpha",
        };
        command.Actions.Add(new NyxIdChatActionReport
        {
            ActionRequestId = actionRequestId,
            OriginTurnId = "turn-alpha",
            Disposition = NyxIdChatActionDisposition.Completed,
            Resource = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef
                {
                    UserServiceId = "service-alpha",
                },
            },
        });
        return command;
    }

    private static NyxIdChatConversationGAgentState CreateBlockedActionState()
    {
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-tool-alpha",
            OperationId = "operation-tool-alpha",
            OperationGeneration = 1,
        };
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTaskStatus.Active,
            },
            ProgressSequence = 1,
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Waiting,
            Required = true,
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Succeeded,
            },
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource { ToolName = "nyxid_proxy" },
            },
        });
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = key,
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                Receipt = new AgentToolReceipt
                {
                    Status = AgentToolReceiptStatus.AuthorizationRequired,
                    AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                    {
                        ServiceSlug = "api-github",
                        ReasonCode = "NYXID_UNAUTHORIZED",
                        SafeMessage = "Connect GitHub.",
                    },
                },
            },
        };
        return NyxIdChatBrowserActions.RequestAuthorization(
            state,
            signal,
            CreateActionRegistry(),
            Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero))).State;
    }

    private static NyxIdChatConversationGAgentState CreatePendingHistoryTerminalState(
        NyxIdChatTurnStatus status,
        string text,
        string errorCode)
    {
        var observedAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var turn = new NyxIdChatTurnState
        {
            TurnId = "turn-terminal-alpha",
            TaskId = "task-terminal-alpha",
            Status = status,
            TerminalAt = observedAt.Clone(),
        };
        return new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = turn.TurnId,
                TaskId = turn.TaskId,
                Status = status switch
                {
                    NyxIdChatTurnStatus.Succeeded => NyxIdChatTaskStatus.Succeeded,
                    NyxIdChatTurnStatus.Failed => NyxIdChatTaskStatus.Failed,
                    NyxIdChatTurnStatus.Stopped => NyxIdChatTaskStatus.Stopped,
                    NyxIdChatTurnStatus.Blocked => NyxIdChatTaskStatus.Blocked,
                    _ => NyxIdChatTaskStatus.Unspecified,
                },
            },
            HistoryDeliveryReservation = new NyxIdChatHistoryDeliveryReservationState
            {
                DeliveryId = "delivery-terminal-alpha",
                TurnId = turn.TurnId,
                SourceActorId = "conversation-alpha",
                SourceCommandId = "command-terminal-alpha",
                Dispatched = true,
                DispatchedAt = observedAt.Clone(),
                Attempt = 1,
            },
            PendingHistoryTerminal = new NyxIdChatHistoryTerminalOutbox
            {
                DeliveryId = "delivery-terminal-alpha",
                TurnId = turn.TurnId,
                SourceActorId = "conversation-alpha",
                SourceCommandId = "command-terminal-alpha",
                Status = status,
                Text = text,
                ErrorCode = errorCode,
                ObservedAt = observedAt,
                Attempt = 1,
            },
        };
    }

    private static Task PersistActionStateAsync(
        IEventStore eventStore,
        string actorId,
        NyxIdChatConversationGAgentState state) =>
        eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "action-state-alpha",
                    AgentId = actorId,
                    Version = 1,
                    Timestamp = Timestamp.FromDateTimeOffset(
                        new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
                    EventType = NyxIdChatActionRequestedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatActionRequestedEvent
                    {
                        Request = state.PendingActions.Single().Clone(),
                        Task = state.ActiveTask.Clone(),
                        OriginTurn = state.ActiveTurn.Clone(),
                        State = state.Clone(),
                    }),
                },
            ],
            expectedVersion: 0);

    private static Task PersistTestStateAsync(
        IEventStore eventStore,
        string actorId,
        long version,
        NyxIdChatConversationGAgentState state) =>
        eventStore.AppendAsync(
            actorId,
            [
                new StateEvent
                {
                    EventId = "test-state-replacement-alpha",
                    AgentId = actorId,
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(
                        new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
                    EventType = NyxIdChatTurnStartedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new NyxIdChatTurnStartedEvent { State = state.Clone() }),
                },
            ],
            expectedVersion: version - 1);

    private static NyxIdChatConversationGAgentState UnpackControllerState(StateEvent stateEvent)
    {
        if (stateEvent.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            return stateEvent.EventData.Unpack<NyxIdChatOperationReconciledEvent>().State;
        if (stateEvent.EventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor))
            return stateEvent.EventData.Unpack<NyxIdChatControlFenceCommittedEvent>().State;
        if (stateEvent.EventData.Is(NyxIdChatActionRequestedEvent.Descriptor))
            return stateEvent.EventData.Unpack<NyxIdChatActionRequestedEvent>().State;
        if (stateEvent.EventData.Is(NyxIdChatStepControlCommittedEvent.Descriptor))
            return stateEvent.EventData.Unpack<NyxIdChatStepControlCommittedEvent>().State;

        throw new InvalidOperationException(
            $"Committed event '{stateEvent.EventType}' does not contain a controller state snapshot.");
    }

    private static NyxIdChatConversationGAgent CreateController(
        ServiceProvider services,
        string actorId,
        IActorDispatchPort? actorDispatchPort = null,
        AgentProfileTurnCatalogMaterializer? turnCatalogMaterializer = null)
    {
        var operations = new List<string>();
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            actorDispatchPort ??
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            turnCatalogMaterializer)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static ServiceProvider BuildEventSourcingServices(
        IEventStore eventStore,
        IChatHistoryCommandPort? historyCommandPort = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null,
        NyxIdAssistantActionRegistry? actionRegistry = null,
        IGAgentActorRegistryCommandPort? registryCommandPort = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton(actionRegistry ?? CreateActionRegistry())
            .AddSingleton(
                registryCommandPort ?? new RecordingGAgentActorRegistryCommandPort())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(
                callbackScheduler ?? new NoopRuntimeCallbackScheduler())
            .AddSingleton<IAuditTrailAppender, AppendedAuditTrail>()
            .AddSingleton<IAuditActorIdentityHasher, StableIdentityHasher>()
            .AddSingleton<IAgentToolAdmissionLedger>(AlwaysStartingAgentToolAdmissionLedger.Instance)
            .AddSingleton<IAgentToolExecutionPort, AdmittedAgentToolExecutor>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        if (historyCommandPort is not null)
            services.AddSingleton(historyCommandPort);
        else
            services.AddSingleton<IChatHistoryCommandPort>(new RecordingChatHistoryCommandPort([]));
        return services.BuildServiceProvider();
    }

    private sealed class RecordingGAgentActorRegistryCommandPort : IGAgentActorRegistryCommandPort
    {
        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
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

    private sealed class FixedProfileClassifier(string intentId) : IAgentProfileTurnClassifier
    {
        public List<AgentProfileTurnClassificationRequest> Requests { get; } = [];

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(AgentProfileTurnClassificationResult.Matched(intentId));
        }
    }

    private sealed class FixedLlmProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => [provider.Name];
    }

    private sealed class NaturalServiceConnectClassifierProvider : ILLMProvider
    {
        public string Name => "natural-service-connect-classifier-test";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaContent = "{\"status\":\"matched\",\"intent_id\":\"service_connect\"}",
                IsLast = true,
            };
            await Task.CompletedTask;
        }
    }

    private sealed class FixedSkillFetcher(
        string guid,
        string version,
        string name,
        string publisherId,
        ByteString skillHash,
        string markdown) : IExactRemoteSkillFetcher
    {
        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ExactRemoteSkillFetchResult.Success(
                guid,
                version,
                name,
                publisherId,
                skillHash,
                markdown));
        }
    }

    private sealed class FixedToolSetRegistry(
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
                    "Unknown test tool set.",
                    [name]));
    }

    private sealed class FixedToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(tools);
        }
    }

    private sealed class CanonicalProfileTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => $"{name} test boundary";
        public string ParametersSchema => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class VerifiedServiceConnectTool : IAgentTool
    {
        public string Name => "nyxid_require_service";
        public string Description => "Require a caller-visible NyxID service.";
        public string ParametersSchema =>
            "{\"type\":\"object\",\"properties\":{\"service_slug\":{\"type\":\"string\"}}}";
        public bool IsReadOnly => true;
        public int ExecutionCount { get; private set; }
        public string? SourceReadableBearerToken { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionCount++;
            SourceReadableBearerToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                AgentToolRequestContext.Current?.Credentials);
            return Task.FromResult(
                "{\"blocked\":true,\"service_slug\":\"aws-cost-explorer\",\"reason_code\":\"NYXID_SERVICE_REGISTRATION_REQUIRED\"}");
        }

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
                ErrorCode = "NYXID_SERVICE_REGISTRATION_REQUIRED",
                ErrorMessage = "Connect GitHub to continue.",
                AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                {
                    ServiceSlug = "aws-cost-explorer",
                    ReasonCode = "NYXID_SERVICE_REGISTRATION_REQUIRED",
                    SafeMessage = "Connect AWS Cost Explorer to continue.",
                },
            };
    }

    private sealed class ServiceConnectToolCallProvider : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "canonical-profile-test";
        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-require-aws-cost-explorer",
                    Name = "nyxid_require_service",
                    ArgumentsJson = "{\"service_slug\":\"aws-cost-explorer\"}",
                },
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                FinishReason = "tool_calls",
                IsLast = true,
            };
        }
    }

    private static NyxIdAssistantActionRegistry CreateActionRegistry() =>
        NyxIdAssistantActionRegistry.Load("""
        {
          "schema_version": 4,
          "revision": "nyxid-assistant-actions.v4",
          "actions": [
            {
              "action": "service.connect",
              "description": "Connect a catalog service in NyxID.",
              "params_schema": {
                "oneOf": [
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["catalogService"],
                    "properties": {
                      "catalogService": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["serviceSlug"],
                        "properties": {
                          "serviceSlug": {"type": "string"},
                          "requestedScopes": {"type": "array", "items": {"type": "string"}},
                          "viaNodeId": {"type": "string"},
                          "targetOrgId": {"type": "string"}
                        }
                      }
                    }
                  },
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["customService"],
                    "properties": {
                      "customService": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["name", "endpointUrl", "authMethod"],
                        "properties": {
                          "name": {"type": "string"},
                          "endpointUrl": {"type": "string"},
                          "authMethod": {"type": "string"},
                          "authKeyName": {"type": "string"},
                          "viaNodeId": {"type": "string"},
                          "targetOrgId": {"type": "string"}
                        }
                      }
                    }
                  }
                ]
              },
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": true
            }
          ]
        }
        """);

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) => new()
    {
        Id = "envelope-alpha",
        Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = actorId } },
        Propagation = new EnvelopePropagation { CorrelationId = "correlation-alpha" },
    };

    private static void AssignActorId(GAgentBase agent, string actorId) =>
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DueTime = request.DueTime,
                DeliveryMode = request.DeliveryMode,
            });
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(
            string actorId,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorRuntime(
        List<string> operations,
        string failedStage = "") : IActorRuntime
    {
        public List<(Type Type, string Id)> CreateCalls { get; } = [];
        public List<(string ParentId, string ChildId)> LinkCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            operations.Add("create");
            if (failedStage == "create")
                throw new InvalidOperationException("create failed with bearer-secret");
            CreateCalls.Add((agentType, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add("link");
            if (failedStage == "link")
                throw new InvalidOperationException("link failed with bearer-secret");
            LinkCalls.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort(
        List<string> operations,
        Func<string, EventEnvelope, Task> onDispatch,
        string failedStage = "")
        : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];
        public List<(string ActorId, EventEnvelope Envelope)> OperationCalls =>
            Calls.Where(static call =>
                    call.Envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor))
                .ToList();
        public List<(string ActorId, EventEnvelope Envelope)> StartTurnCalls =>
            Calls.Where(static call =>
                    call.Envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor))
                .ToList();
        public List<(string ActorId, EventEnvelope Envelope)> RecoveryCalls =>
            Calls.Where(static call =>
                    call.Envelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor))
                .ToList();

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var isOperationDispatch =
                envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor);
            if (!envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor))
                operations.Add("dispatch");
            Calls.Add((actorId, envelope.Clone()));
            if (failedStage == "dispatch" && isOperationDispatch)
                throw new InvalidOperationException("dispatch failed with bearer-secret");
            await onDispatch(actorId, envelope);
            return failedStage == "dispatch-rejected" && isOperationDispatch
                ? new DispatchAdmission(
                    false,
                    envelope.Id,
                    DateTimeOffset.UtcNow,
                    actorId,
                    envelope.Propagation?.CorrelationId ?? envelope.Id)
                : DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class RecordingChatHistoryCommandPort(
        List<string> operations,
        Func<ChatHistoryTurnDeliveryReservation, Task>? onReserve = null)
        : IChatHistoryCommandPort
    {
        public Exception? ReserveException { get; set; }
        public Exception? NotifyException { get; set; }
        public List<ChatHistoryTurnDeliveryReservation> Reservations { get; } = [];
        public List<ChatHistoryTurnTerminalNotification> Notifications { get; } = [];

        public Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default) => Task.CompletedTask;

        public async Task ReserveTurnDeliveryAsync(
            ChatHistoryTurnDeliveryReservation request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add("history.reserve");
            Reservations.Add(request);
            if (ReserveException is not null)
                throw ReserveException;
            if (onReserve is not null)
                await onReserve(request);
        }

        public Task NotifyTurnTerminalAsync(
            ChatHistoryTurnTerminalNotification notification,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Notifications.Add(notification);
            return NotifyException is null
                ? Task.CompletedTask
                : Task.FromException(NotifyException);
        }

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default) =>
            Task.FromResult(ChatHistoryDeleteResult.Accepted());
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
        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
