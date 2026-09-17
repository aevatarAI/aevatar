using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.AGUI.Contracts;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Runtime.Propagation;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Type = System.Type;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatConversationGAgentTests
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
    public async Task CreateConversation_HistoryInitializationFailure_ShouldResumeFirstTurnOnceAfterRetry()
    {
        const string actorId = "nyxid-chat-first-turn-retry";
        var operations = new List<string>();
        var eventStore = new InMemoryEventStoreForTests();
        var history = new RecordingChatHistoryCommandPort(operations)
        {
            InitializeException = new InvalidOperationException("history unavailable"),
        };
        var runtime = new RecordingActorRuntime(operations);
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore, history);
        var agent = new NyxIdChatConversationGAgent(
            runtime,
            dispatch,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 8, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        await agent.ActivateAsync();
        var firstTurn = WithOwner(CreateStartTurnCommand(), "owner-alpha");
        firstTurn.ConversationActorId = actorId;
        var create = new NyxIdChatConversationCreateCommand
        {
            ScopeId = "scope-alpha",
            CreatedLocally = true,
            RequestedActorId = actorId,
            FirstTurn = firstTurn,
        };

        await agent.HandleCreateConversationAsync(create);
        var initialHistorySignal = dispatch.Calls
            .Last(call => call.Envelope.Payload.Is(
                NyxIdChatHistoryInitializationDispatchRequested.Descriptor))
            .Envelope.Payload
            .Unpack<NyxIdChatHistoryInitializationDispatchRequested>();
        await agent.HandleHistoryInitializationDispatchRequestedAsync(initialHistorySignal);

        agent.State.ActiveTurn.Should().BeNull();
        agent.State.PendingHistoryInitialization.Should().NotBeNull();
        agent.State.PendingCreationFirstTurn.Should().NotBeNull();
        agent.State.ToString().Should().NotContain("runtime-token-alpha");
        history.Reservations.Should().BeEmpty();
        dispatch.OperationCalls.Should().BeEmpty();

        history.InitializeException = null;
        var pending = agent.State.PendingHistoryInitialization.Clone();
        await agent.HandleHistoryInitializationDispatchRequestedAsync(
            new NyxIdChatHistoryInitializationDispatchRequested
            {
                OperationId = pending.OperationId,
                Attempt = pending.Attempt,
            });
        await DispatchPendingCreationFirstTurnAsync(agent, dispatch);

        operations.Should().Equal(
            "dispatch",
            "history.initialize",
            "history.initialize",
            "dispatch",
            "history.reserve",
            "create",
            "link",
            "dispatch");
        agent.State.ActiveTurn.TurnId.Should().Be("turn-alpha");
        agent.State.PendingHistoryInitialization.Should().BeNull();
        agent.State.PendingCreationFirstTurn.Should().BeNull();
        history.Reservations.Should().ContainSingle();
        dispatch.OperationCalls.Should().ContainSingle();

        await agent.HandleHistoryInitializationDispatchRequestedAsync(
            new NyxIdChatHistoryInitializationDispatchRequested
            {
                OperationId = pending.OperationId,
                Attempt = pending.Attempt,
            });
        await agent.HandleCreateConversationAsync(create.Clone());

        history.Reservations.Should().ContainSingle();
        dispatch.OperationCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task WorkflowInteractiveActionHandoff_ShouldCreateActionOnlyStateAndRejectConflictingReplay()
    {
        const string actorId = "nyxid-chat-workflow-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId);
        await agent.ActivateAsync();
        var command = new WorkflowInteractiveActionHandoffCommand
        {
            HandoffId = "handoff-alpha",
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            SourceWorkflowActorId = "workflow-run-alpha",
            Request = new WorkflowInteractiveActionRequestWirePayload
            {
                SchemaVersion = 4,
                ActorId = actorId,
                OriginTurnId = "turn-studio-alpha",
                TaskId = "task-action-alpha",
                StepId = "step-action-alpha",
                ActionRequestId = "action-request-alpha",
                Action = "service.connect",
                Params = new WorkflowInteractiveActionParams
                {
                    CatalogService = new WorkflowInteractiveCatalogServiceActionParams
                    {
                        ServiceSlug = "api-github",
                        RequestedScopes = { "repo" },
                    },
                },
            },
        };

        await agent.HandleWorkflowInteractiveActionHandoffAsync(command);

        agent.State.ConversationActorId.Should().Be(actorId);
        agent.State.ScopeId.Should().Be("scope-alpha");
        agent.State.OwnerSubject.Should().Be("owner-alpha");
        agent.State.ActiveTurn.TurnId.Should().Be("turn-studio-alpha");
        agent.State.ActiveTurn.TaskId.Should().Be("task-action-alpha");
        agent.State.ActiveTask.TurnId.Should().Be("turn-studio-alpha");
        agent.State.ActiveTask.TaskId.Should().Be("task-action-alpha");
        var action = agent.State.PendingActions.Should().ContainSingle().Which;
        action.ConversationActorId.Should().Be(actorId);
        action.ActionRequestId.Should().Be("action-request-alpha");
        action.Action.Should().Be(NyxIdAssistantActionKind.ServiceConnect);
        action.Params.CatalogServiceConnect.ServiceSlug.Should().Be("api-github");
        action.Params.CatalogServiceConnect.RequestedScopes.Should().Equal("repo");
        var committedCount = (await eventStore.GetEventsAsync(actorId)).Count;

        await agent.HandleWorkflowInteractiveActionHandoffAsync(command.Clone());

        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(committedCount);

        var conflicting = command.Clone();
        conflicting.Request.Params.CatalogService.RequestedScopes.Clear();
        conflicting.Request.Params.CatalogService.RequestedScopes.Add("read:org");
        var act = () => agent.HandleWorkflowInteractiveActionHandoffAsync(conflicting);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*identity was reused with different content*");
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(committedCount);
    }

    [Fact]
    public async Task WorkflowInteractiveKeyCreateHandoff_ShouldValidateRegistryBeforeFirstEvent()
    {
        const string actorId = "nyxid-chat-workflow-key-create";
        var invalidStore = new InMemoryEventStoreForTests();
        using var invalidServices = BuildEventSourcingServices(
            invalidStore,
            actionRegistry: CreateLeastScopeActionRegistry());
        var invalidAgent = CreateController(invalidServices, actorId);
        await invalidAgent.ActivateAsync();
        var invalid = KeyCreateHandoff(actorId);
        invalid.Request.Params.KeyCreate.AllowedServiceIds.Clear();

        var invalidAct = () => invalidAgent.HandleWorkflowInteractiveActionHandoffAsync(invalid);

        await invalidAct.Should().ThrowAsync<NyxIdAssistantActionRegistryException>();
        (await invalidStore.GetEventsAsync(actorId)).Should().BeEmpty();

        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(
            eventStore,
            actionRegistry: CreateLeastScopeActionRegistry());
        var agent = CreateController(services, actorId);
        await agent.ActivateAsync();
        var valid = KeyCreateHandoff(actorId);

        await agent.HandleWorkflowInteractiveActionHandoffAsync(valid);

        var action = agent.State.PendingActions.Should().ContainSingle().Which;
        action.Action.Should().Be(NyxIdAssistantActionKind.KeyCreate);
        action.Params.KeyCreate.Name.Should().Be("agent-alpha");
        action.Params.KeyCreate.Platform.Should().Be("codex");
        action.Params.KeyCreate.AllowedServiceIds.Should().Equal("m-github", "m-lark");
        action.RegistryRevision.Should().Be("nyxid-assistant-actions.v6");
    }

    private static WorkflowInteractiveActionHandoffCommand KeyCreateHandoff(string actorId) =>
        new()
        {
            HandoffId = "handoff-key-create",
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            SourceWorkflowActorId = "workflow-run-alpha",
            Request = new WorkflowInteractiveActionRequestWirePayload
            {
                SchemaVersion = 4,
                ActorId = actorId,
                OriginTurnId = "turn-studio-alpha",
                TaskId = "task-key-create",
                StepId = "step-key-create",
                ActionRequestId = "action-key-create",
                Action = "key.create",
                Params = new WorkflowInteractiveActionParams
                {
                    KeyCreate = new WorkflowInteractiveKeyCreateActionParams
                    {
                        Name = "agent-alpha",
                        Platform = "codex",
                        AllowedServiceIds = { "m-github", "m-lark" },
                    },
                },
            },
        };

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
        var operationDispatchPort = assembly.GetType(
            "Aevatar.GAgents.NyxidChat.INyxIdChatTurnOperationDispatchPort");

        operationDispatchPort.Should().NotBeNull();
        typeof(NyxIdChatConversationGAgent).GetConstructor(
        [
            typeof(IActorRuntime),
            typeof(IActorDispatchPort),
            typeof(TimeProvider),
        ]).Should().NotBeNull();
        typeof(NyxIdChatTurnGAgent).GetConstructor(
        [
            operationDispatchPort!,
            typeof(IActorDispatchPort),
            typeof(NyxIdToolOptions),
            typeof(TimeProvider),
            typeof(ISecretVault),
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
        command.InputPartsFingerprint = "raw-inline-input-fingerprint";
        command.InputParts.Add(new ChatContentPart
        {
            Kind = ChatContentPartKind.Image,
            MediaType = "image/png",
            Name = "invoice.png",
            FileRef = new Aevatar.AI.Abstractions.ChatFileRef
            {
                FileId = "artifact-file-first",
                ArtifactId = "workflow-file://artifact-file-first",
                SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput,
                Sha256 = "content-sha",
            },
        });
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, command));
        var stateAfterFirst = agent.State.ToByteArray();
        var operationsAfterFirst = operations.ToArray();
        var eventCountAfterFirst = (await eventStore.GetEventsAsync(conversationActorId)).Count;
        var reservationCountAfterFirst = history.Reservations.Count;
        var createCountAfterFirst = runtime.CreateCalls.Count;
        var linkCountAfterFirst = runtime.LinkCalls.Count;
        var dispatchCountAfterFirst = dispatch.Calls.Count;

        var replay = command.Clone();
        replay.InputParts[0].FileRef.FileId = "artifact-file-retry";
        replay.InputParts[0].FileRef.ArtifactId = "workflow-file://artifact-file-retry";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, replay));

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
    public async Task StartTurn_UnprofiledServiceConnect_ShouldCommitAndDispatchTypedIntent()
    {
        const string conversationActorId = "conversation-service-connect-intent";
        var classifier = new RecordingTurnIntentClassifier(NyxIdChatTurnIntent.ServiceConnect);
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(
            services,
            conversationActorId,
            dispatch,
            turnIntentClassifier: classifier);
        await agent.ActivateAsync();
        var start = CreateStartTurnCommand();
        start.ConversationActorId = conversationActorId;
        start.Prompt = "Connect GitHub and verify the connection";
        start.LlmControl = new LLMControlContextPayload
        {
            NyxIdAccessToken = "caller-token",
            ModelOverride = "model-a",
        };
        start.SteeringExecutionContext = new NyxIdChatSteeringExecutionContext
        {
            OriginPrompt = "caller-forged execution context",
            TaskId = "forged-task",
            InputResolutions =
            {
                new NyxIdChatSteeringInputResolutionFact
                {
                    RequestId = "forged-input",
                    Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
                    Answer = new NyxIdChatInputAnswer
                    {
                        FreeText = "caller-forged committed answer",
                    },
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, start));

        classifier.UserMessages.Should().Equal("Connect GitHub and verify the connection");
        classifier.RequestIds.Should().Equal("turn-alpha");
        classifier.LlmControls.Should().ContainSingle().Which.Should().BeEquivalentTo(
            LLMControlContextMapper.FromPayload(start.LlmControl));
        agent.State.ActiveTurn.Intent.Should().Be(NyxIdChatTurnIntent.ServiceConnect);
        var command = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        command.Llm.Intent.Should().Be(NyxIdChatTurnIntent.ServiceConnect);
        command.Llm.AgentProfile.Should().BeNull();
        command.Llm.AgentProfileTurnAuthority.Should().BeNull();
        command.Llm.Request.Prompt.Should().Be("Connect GitHub and verify the connection");
        agent.State.ToString().Should()
            .NotContain("caller-forged execution context")
            .And.NotContain("caller-forged committed answer");
    }

    [Fact]
    public async Task StartTurn_EnforcedServiceConnectProfile_ShouldMapCommittedRouteWithoutReclassification()
    {
        const string conversationActorId = "conversation-profile-service-connect-intent";
        const string prompt = "Connect GitHub and verify the connection";
        IAgentTool[] routeTools =
        [
            new CanonicalProfileTool("nyxid_catalog"),
            new CanonicalProfileTool("nyxid_require_service"),
        ];
        var profileClassifier = new FixedProfileClassifier(
            NyxIdChatTurnIntentClassifier.ServiceConnectIntentId);
        var materializer = new AgentTurnToolCatalogMaterializer(
            new FixedToolSetRegistry("profile.route", new FixedToolSource(routeTools)),
            profileClassifier);
        var independentClassifier = new RecordingTurnIntentClassifier(NyxIdChatTurnIntent.Unspecified);
        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-service-connect",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_catalog", "nyxid_require_service" },
            },
            RecoveryToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_catalog" },
            },
            ClassifierTimeoutMs = 1_000,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.Members.Add(new AgentProfileSkillMember
        {
            IntentId = NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
            RoutingDescription = "Connect a hosted external service account.",
            SkillRef = new ExactRemoteSkillRef
            {
                Guid = "service-connect-skill",
                LiteralVersion = "1.0",
            },
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_require_service" },
            },
            SideEffectClass = AgentProfileSideEffectClass.ExternalHandoff,
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
            materializer,
            turnIntentClassifier: independentClassifier);
        await agent.ActivateAsync();
        var start = WithOwner(CreateStartTurnCommand(), "owner-alpha");
        start.ConversationActorId = conversationActorId;
        start.Prompt = prompt;

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
        await DispatchPendingCreationFirstTurnAsync(agent, dispatch);

        profileClassifier.Requests.Should().ContainSingle();
        independentClassifier.UserMessages.Should().BeEmpty();
        agent.State.ActiveTurn.AgentProfileTurnAuthority.CandidateRoute.IntentId.Should()
            .Be(NyxIdChatTurnIntentClassifier.ServiceConnectIntentId);
        agent.State.ActiveTurn.Intent.Should().Be(NyxIdChatTurnIntent.ServiceConnect);
        var command = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        command.Llm.Intent.Should().Be(NyxIdChatTurnIntent.ServiceConnect);
    }

    [Fact]
    public async Task StartTurn_EnforcedConnectedServiceOperation_ShouldCommitOrdinaryExactRoute()
    {
        const string conversationActorId = "conversation-connected-service-write";
        const string connectedServiceIntentId = "connected-service-write";
        const string prompt =
            "Use exact UserService us-lark-alpha endpoint im-message-create through " +
            "tool lark-message-create.";
        IAgentTool[] routeTools = [new CanonicalProfileTool("lark-message-create")];
        var classifierProvider = new ExactConnectedServiceRoutingClassifierProvider(
            connectedServiceIntentId);
        var profileClassifier = new StreamingAgentProfileTurnClassifier(
            new FixedLlmProviderFactory(classifierProvider));
        var materializer = new AgentTurnToolCatalogMaterializer(
            new FixedToolSetRegistry("profile.route", new FixedToolSource(routeTools)),
            profileClassifier);
        var independentClassifier =
            new RecordingTurnIntentClassifier(NyxIdChatTurnIntent.ServiceConnect);
        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-mainnet-connected-service",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "lark-message-create" },
            },
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ClassifierTimeoutMs = 1_000,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.Members.Add(new AgentProfileSkillMember
        {
            IntentId = connectedServiceIntentId,
            RoutingDescription = "Write through an already-connected exact UserService operation.",
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "lark-message-create" },
            },
            SideEffectClass = AgentProfileSideEffectClass.ExternalHandoff,
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
            materializer,
            turnIntentClassifier: independentClassifier);
        await agent.ActivateAsync();
        var start = WithOwner(CreateStartTurnCommand(), "owner-alpha");
        start.ConversationActorId = conversationActorId;
        start.Prompt = prompt;

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
        await DispatchPendingCreationFirstTurnAsync(agent, dispatch);

        classifierProvider.Requests.Should().HaveCount(2);
        using var phaseOneInput = JsonDocument.Parse(classifierProvider.Requests[0].Messages
            .Single(static message => message.Role == "user").Content!);
        phaseOneInput.RootElement.GetProperty("intents").EnumerateArray()
            .Select(static candidate => candidate.GetProperty("intent_id").GetString()).Should().Equal(
            NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
            NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
            NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
            NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId,
            AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId);
        phaseOneInput.RootElement.GetProperty("intents")[0]
            .GetProperty("routing_description").GetString().Should()
            .Contain("already-connected exact UserService");
        using var phaseTwoInput = JsonDocument.Parse(classifierProvider.Requests[1].Messages
            .Single(static message => message.Role == "user").Content!);
        phaseTwoInput.RootElement.GetProperty("intents").EnumerateArray()
            .Select(static candidate => candidate.GetProperty("intent_id").GetString())
            .Should().Equal(connectedServiceIntentId);
        independentClassifier.UserMessages.Should().BeEmpty();
        agent.State.ActiveTurn.AgentProfileTurnAuthority.CandidateRoute.IntentId.Should()
            .Be(connectedServiceIntentId);
        agent.State.ActiveTurn.AgentProfileTurnAuthority.AuthorityCeilingToolNames.Should()
            .Equal("lark-message-create");
        agent.State.ActiveTurn.Intent.Should().Be(NyxIdChatTurnIntent.Unspecified);
        var command = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        command.Llm.Intent.Should().Be(NyxIdChatTurnIntent.Unspecified);
        command.Llm.AgentProfileTurnAuthority.CandidateRoute.IntentId.Should()
            .Be(connectedServiceIntentId);
        command.Llm.AgentProfileTurnAuthority.AuthorityCeilingToolNames.Should()
            .Equal("lark-message-create");

        var admission = CreateConnectedServiceWriteAdmission();
        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(
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
                            CallId = "call-connected-service-write",
                            ToolName = "lark-message-create",
                            ArgumentsJson = "{\"receiveId\":\"chat-alpha\",\"text\":\"weekly update\"}",
                            Safety = new NyxIdChatToolCallSafety
                            {
                                SideEffectKind = "lark.message.create",
                                MayChangeExternalState = true,
                            },
                            NyxIdProvenance = new NyxIdOperationRef
                            {
                                ConnectedServiceId = admission.ServiceInstanceId,
                                ServiceSlug = admission.ServiceSlug,
                                CatalogServiceSlug = "lark",
                                OperationId = admission.PublishedEndpoint.EndpointId,
                                ReadinessCapabilityId = "lark-message-create-ready",
                            },
                            OperationAdmission = admission.Clone(),
                        },
                    },
                },
            }));

        dispatch.OperationCalls.Should().HaveCount(2,
            "the exact connected-service operation dispatches immediately after the LLM result");
        var tool = dispatch.OperationCalls[^1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        tool.InputCase.Should().Be(NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        tool.Tool.OperationAdmission.Should().BeEquivalentTo(admission);
        tool.Tool.OperationAdmission.ServiceInstanceId.Should().Be("connected-service-lark");
        tool.Tool.OperationAdmission.PublishedEndpoint.EndpointId.Should()
            .Be("lark-message-create");
        tool.Tool.OperationAdmission.CatalogDigest.Should()
            .Be($"sha256:{new string('a', 64)}");
        tool.Tool.OperationAdmission.ContractDigest.Should().Be(new string('b', 64));
        tool.Tool.OperationAdmission.ExecutionPolicy.Approval.Should().Be(
            AgentToolOperationApprovalPayload.Required);
    }

    [Fact]
    public async Task StartTurn_EnforcedGeneralProfile_ShouldSelectServiceConnectAgainstBroadProfileIntent()
    {
        const string conversationActorId = "conversation-profile-general-connect-intent";
        const string generalIntentId = "general_nyxid_assistant";
        const string prompt = "Connect GitHub and verify the connection";
        IAgentTool[] routeTools =
        [
            new CanonicalProfileTool("use_skill"),
            new CanonicalProfileTool("nyxid_services"),
            new CanonicalProfileTool("nyxid_catalog"),
            new CanonicalProfileTool("nyxid_require_service"),
            new CanonicalProfileTool("github_get_current_user"),
        ];
        var profileClassifier = new FixedProfileClassifier(
            NyxIdChatTurnIntentClassifier.ServiceConnectIntentId);
        var materializer = new AgentTurnToolCatalogMaterializer(
            new FixedToolSetRegistry("profile.route", new FixedToolSource(routeTools)),
            profileClassifier);
        var serverClassifier = new RecordingTurnIntentClassifier(NyxIdChatTurnIntent.Unspecified);
        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-mainnet-general",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "nyxid_catalog", "nyxid_require_service" },
            },
            ClassifierTimeoutMs = 1_000,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.MaximumToolPolicy.ToolNames.Add(routeTools.Select(static tool => tool.Name));
        profile.Members.Add(new AgentProfileSkillMember
        {
            IntentId = generalIntentId,
            RoutingDescription = "Handle ordinary NyxID assistant requests.",
            SkillRef = new ExactRemoteSkillRef
            {
                Guid = "general-nyxid-skill",
                LiteralVersion = "1.0",
            },
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames =
                {
                    "use_skill",
                    "nyxid_services",
                    "nyxid_catalog",
                    "nyxid_require_service",
                    "github_get_current_user",
                },
            },
            SideEffectClass = AgentProfileSideEffectClass.ExternalHandoff,
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
            materializer,
            turnIntentClassifier: serverClassifier);
        await agent.ActivateAsync();
        var start = WithOwner(CreateStartTurnCommand(), "owner-alpha");
        start.ConversationActorId = conversationActorId;
        start.Prompt = prompt;

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
        await DispatchPendingCreationFirstTurnAsync(agent, dispatch);

        var classification = profileClassifier.Requests.Should().ContainSingle().Which;
        classification.Candidates.Select(static candidate => candidate.IntentId).Should().Equal(
            NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
            NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
            NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
            NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId,
            AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId);
        serverClassifier.UserMessages.Should().BeEmpty();
        agent.State.ActiveTurn.AgentProfileTurnAuthority.CandidateRoute.IntentId.Should()
            .Be(NyxIdChatTurnIntentClassifier.ServiceConnectIntentId);
        agent.State.ActiveTurn.AgentProfileTurnAuthority.SelectedExactSkillRef.Should().BeNull();
        agent.State.ActiveTurn.AgentProfileTurnAuthority.AuthorityCeilingToolNames.Should()
            .BeEquivalentTo("nyxid_catalog", "nyxid_require_service");
        agent.State.ActiveTurn.Intent.Should().Be(NyxIdChatTurnIntent.ServiceConnect);
        var command = dispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        command.Llm.Intent.Should().Be(NyxIdChatTurnIntent.ServiceConnect);
        command.Llm.AgentProfileTurnAuthority.CandidateRoute.IntentId.Should()
            .Be(NyxIdChatTurnIntentClassifier.ServiceConnectIntentId);
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
        await DispatchPendingCreationFirstTurnAsync(agent, dispatch);

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
        await DispatchPendingCreationFirstTurnAsync(agent, dispatch);

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
        var catalogService = new VerifiedServiceCatalogTool();
        var requireService = new VerifiedServiceConnectTool();
        IAgentTool[] routeTools =
        [
            new CanonicalProfileTool("nyxid_service_inventory"),
            catalogService,
            requireService,
        ];
        var classifierProvider = new NaturalServiceConnectClassifierProvider();
        var classifier = new StreamingAgentProfileTurnClassifier(
            new FixedLlmProviderFactory(classifierProvider));
        var materializer = new AgentTurnToolCatalogMaterializer(
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
        await DispatchPendingCreationFirstTurnAsync(agent, dispatch);

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
        dispatch.OperationCalls.Should().HaveCount(3);
        var continuationCommand = dispatch.OperationCalls[2].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        var continuationExecution = await turnExecutor.ExecuteAsync(
            continuationCommand,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            continuationExecution.Result));
        dispatch.OperationCalls.Should().HaveCount(4);
        var requireServiceCommand = dispatch.OperationCalls[3].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        var requireServiceExecution = await turnExecutor.ExecuteAsync(
            requireServiceCommand,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            requireServiceExecution.Result));

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
        var intents = classificationDocument.RootElement.GetProperty("intents");
        intents.EnumerateArray()
            .Select(static intent => intent.GetProperty("intent_id").GetString())
            .Should().Equal(
                NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
                NyxIdChatTurnIntentClassifier.KeyCreateIntentId,
                NyxIdChatTurnIntentClassifier.KeyRotateIntentId,
                NyxIdChatTurnIntentClassifier.WorkflowAuthoringIntentId,
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId);
        intents[0]
            .GetProperty("side_effect_class").GetString().Should().Be("external_handoff");
        provider.Requests.Should().HaveCount(2);
        foreach (var llmRequest in provider.Requests)
        {
            llmRequest.Tools.Should().HaveCount(2);
            llmRequest.Tools.Should().Contain(candidate => ReferenceEquals(candidate, catalogService));
            llmRequest.Tools.Should().Contain(candidate => ReferenceEquals(candidate, requireService));
            llmRequest.Tools.Should().NotContain(static tool =>
                tool.Name == "nyxid_service_inventory");
        }
        provider.Requests[0].Messages.Single(static message => message.Role == "system").Content.Should()
            .Contain("Selected intent: service_connect")
            .And.Contain(selectedSkillPrompt);
        catalogService.ExecutionCount.Should().Be(1);
        requireService.ExecutionCount.Should().Be(1);
        requireService.SourceReadableBearerToken.Should().Be("runtime-token-alpha");
        using (var requireArguments = JsonDocument.Parse(requireService.ArgumentsJson!))
        {
            requireArguments.RootElement.GetProperty("service_slug").GetString().Should()
                .Be("aws-cost-explorer");
            requireArguments.RootElement.GetProperty("requested_scopes")[0].GetString().Should()
                .Be("repo");
        }

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var action = committed
            .Where(static stateEvent =>
                stateEvent.EventData.Is(NyxIdChatActionRequestedEvent.Descriptor))
            .Should().ContainSingle().Which.EventData.Unpack<NyxIdChatActionRequestedEvent>();
        action.Request.SchemaVersion.Should().Be(4);
        action.Request.Action.Should().Be(NyxIdAssistantActionKind.ServiceConnect);
        action.Request.Params.CatalogServiceConnect.ServiceSlug.Should().Be("aws-cost-explorer");
        action.Request.Params.CatalogServiceConnect.RequestedScopes.Should().Equal("repo");
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
        frames.Should().ContainSingle(frame =>
            frame.Custom != null &&
            frame.Custom.Name == NyxIdChatConversationAguiFrameBuilder.ActionRequestEventName,
            "browser-owned actions publish immediately without a plan confirmation gate");
        var finishedFrames = frames.Where(static frame => frame.RunFinished is not null).ToArray();
        finishedFrames.Should().ContainSingle().Which.RunFinished.Status
            .Should().Be(RunCompletionStatus.Blocked);
    }

    [Fact]
    public async Task ProfiledHumanSessionTurn_ShouldInvokePinnedAccountStatusAndSessionsWithoutApproval()
    {
        const string conversationActorId = "conversation-pinned-class-r";
        const string sourceReadableBearer = "source-readable-bearer";
        string[] expectedToolNames = ["nyxid_account", "nyxid_status", "nyxid_sessions"];
        string[] activatedToolNames =
        [
            "nyxid_developer_apps",
            "nyxid_oauth_bindings",
        ];
        var handler = new RecordingNyxIdReadHandler();
        var options = new NyxIdToolOptions { BaseUrl = "https://nyxid.test" };
        using var apiClient = new NyxIdApiClient(options, new HttpClient(handler));
        var assistantSource = new NyxIdAssistantToolSource(options, apiClient);
        var assistantTools = await assistantSource.DiscoverToolsAsync();
        var profileToolNames = expectedToolNames.Concat(activatedToolNames).ToArray();
        const string profileIntentId = "nyxid-account-status-sessions";
        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-pinned-class-r",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ClassifierTimeoutMs = 1_000,
            ExactSkillFetchTimeoutMs = 1_500,
            MaxSelectedSkillBytes = 256,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.MaximumToolPolicy.ToolNames.Add(profileToolNames);
        profile.Members.Add(new AgentProfileSkillMember
        {
            IntentId = profileIntentId,
            RoutingDescription = "Read the caller's NyxID account, status, and sessions.",
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { profileToolNames },
            },
            SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
        });
        profile = AgentProfileSnapshotCodec.Seal(profile);
        var materializer = new AgentTurnToolCatalogMaterializer(
            new FixedToolSetRegistry("profile.route", new FixedToolSource(assistantTools)),
            new ProfileTaskThenIntentClassifier(profileIntentId));
        var provider = new PinnedClassRToolCallProvider(expectedToolNames);
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(
            services,
            conversationActorId,
            dispatch,
            materializer);
        await agent.ActivateAsync();
        var start = CreateStartTurnCommand();
        start.ConversationActorId = conversationActorId;
        start.Prompt = "Show my NyxID account, status, and active sessions.";
        start.LlmControl.NyxIdAccessToken = sourceReadableBearer;
        start.ToolContext.Credentials.NyxIdAccessToken = sourceReadableBearer;
        start.ToolContext.Credentials.NyxIdCredentialKind =
            AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer;
        start.ToolContext.Channel = new AgentToolChannelContextPayload
        {
            Platform = NyxIdChatServiceDefaults.ServiceId,
        };
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
        await DispatchPendingCreationFirstTurnAsync(agent, dispatch);

        var replyGenerator = new NyxIdConversationReplyGenerator(
            provider,
            new BuiltInPromptFloorProvider(),
            toolSources: [new FixedToolSource([new CanonicalProfileTool("forged_ordinary_tool")])],
            toolExecutionPort: services.GetRequiredService<IAgentToolExecutionPort>(),
            nyxIdChatToolSources: [assistantSource]);
        var generationExecutor = new AgentRunReplyGenerationExecutor(
            new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask),
            replyGenerator,
            interactiveReplyCollector: null,
            relayOptions: null,
            logger: NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var turnExecutor = new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            materializer);
        var session = new NyxIdChatTransientExecutionSession();
        var inputCases = new List<NyxIdChatOperationDispatchCommand.InputOneofCase>();

        for (var operationIndex = 0; operationIndex < 7; operationIndex++)
        {
            dispatch.OperationCalls.Should().HaveCountGreaterThan(
                operationIndex,
                $"operation {operationIndex + 1} must be dispatched by the authoritative controller");
            var command = dispatch.OperationCalls[operationIndex].Envelope.Payload
                .Unpack<NyxIdChatOperationDispatchCommand>();
            inputCases.Add(command.InputCase);
            var execution = await turnExecutor.ExecuteAsync(
                command,
                session,
                static (_, _) => Task.CompletedTask,
                CancellationToken.None);
            await agent.HandleEventAsync(CreateEnvelope(conversationActorId, execution.Result));
        }

        dispatch.OperationCalls.Should().HaveCount(7);
        inputCases.Should().Equal(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm);

        provider.Requests.Should().HaveCount(4);
        var firstRequest = provider.Requests[0];
        firstRequest.Tools.Should().NotBeNull();
        var firstTools = firstRequest.Tools!;
        firstTools.Select(static tool => tool.Name).Should().Contain(expectedToolNames);
        firstTools.Select(static tool => tool.Name).Should().Contain(activatedToolNames);
        firstTools.Should().NotContain(static tool => tool.Name == "nyxid_service_accounts");
        firstTools.Should().NotContain(static tool => tool.Name == "forged_ordinary_tool");
        firstTools.Should().NotContain(static tool => tool.Name == "nyxid_proxy");
        var activatedTools = firstTools
            .Where(tool => activatedToolNames.Contains(tool.Name, StringComparer.Ordinal))
            .ToArray();
        activatedTools.Should().HaveCount(activatedToolNames.Length);
        activatedTools.Should().OnlyContain(static tool =>
            tool.ApprovalMode == ToolApprovalMode.NeverRequire &&
            tool.IsReadOnly &&
            !tool.IsDestructive &&
            tool.GetCallSafety("{}").IsReadOnly &&
            !tool.GetCallSafety("{}").IsDestructive);
        foreach (var tool in activatedTools)
        {
            tool.Should().BeAssignableTo<IAgentToolCapabilityDescriptor>();
            ((IAgentToolCapabilityDescriptor)tool).Capabilities.Should()
                .Contain(AgentToolCapabilities.RequiresHumanSession);
        }
        var requestedTools = firstTools
            .Where(tool => expectedToolNames.Contains(tool.Name, StringComparer.Ordinal))
            .ToArray();
        requestedTools.Should().HaveCount(3);
        requestedTools.Should().OnlyContain(static tool =>
            tool.ApprovalMode == ToolApprovalMode.NeverRequire &&
            tool.IsReadOnly &&
            !tool.IsDestructive &&
            tool.GetCallSafety("{}").IsReadOnly &&
            !tool.GetCallSafety("{}").IsDestructive);
        foreach (var tool in requestedTools)
        {
            tool.Should().BeAssignableTo<IAgentToolCapabilityDescriptor>();
            ((IAgentToolCapabilityDescriptor)tool).Capabilities.Should()
                .Contain(AgentToolCapabilities.RequiresHumanSession);
        }
        for (var index = 0; index < expectedToolNames.Length; index++)
        {
            provider.Requests[index + 1].Messages.Should().ContainSingle(message =>
                message.Role == "tool" &&
                message.ToolCallId == $"call-{expectedToolNames[index]}");
        }

        handler.Requests.Should().HaveCount(6);
        handler.Requests.Select(static request => request.PathAndQuery).Should().BeEquivalentTo(
            "/api/v1/users/me",
            "/api/v1/users/me",
            "/api/v1/keys",
            "/api/v1/api-keys",
            "/api/v1/nodes",
            "/api/v1/sessions");
        handler.Requests.Should().OnlyContain(request =>
            request.Method == HttpMethod.Get && request.BearerToken == sourceReadableBearer);

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var reconciliations = committed
            .Where(static item => item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Select(static item => item.EventData.Unpack<NyxIdChatOperationReconciledEvent>())
            .ToArray();
        var toolReconciliations = reconciliations
            .Where(static item =>
                item.Result.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Tool)
            .ToArray();
        toolReconciliations.Should().HaveCount(3);
        toolReconciliations.Select(static item => item.Result.Tool.Receipt.ToolName)
            .Should().Equal(expectedToolNames);
        toolReconciliations.Should().OnlyContain(static item =>
            item.Result.Tool.Receipt.Status == AgentToolReceiptStatus.Success &&
            item.Result.Tool.Receipt.ApprovalMode == AgentToolReceiptApprovalMode.NeverRequire &&
            item.Result.Tool.Receipt.Effect == AgentToolReceiptEffect.ReadOnly &&
            !item.Result.Tool.Receipt.IsDestructive &&
            string.IsNullOrEmpty(item.Result.Tool.Receipt.ApprovalRequestId) &&
            item.Result.Tool.Receipt.AuthorizationRequired == null);
        var durableToolCalls = reconciliations
            .Where(static item =>
                item.Result.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Llm)
            .SelectMany(static item => item.Result.Llm.ToolCalls)
            .ToArray();
        durableToolCalls.Should().HaveCount(3);
        durableToolCalls.Should().OnlyContain(static call =>
            call.Safety != null &&
            call.Safety.IsReadOnly &&
            !call.Safety.IsDestructive &&
            !call.Safety.MayChangeExternalState);
        committed.Should().NotContain(static item =>
            item.EventData.Is(NyxIdChatActionRequestedEvent.Descriptor));

        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        agent.State.ActiveTask.Steps.Where(static step => step.Kind == NyxIdChatStepKind.Tool)
            .Should().HaveCount(3).And.OnlyContain(static step =>
                step.Status == NyxIdChatStepStatus.Done);
        agent.State.PendingApproval.Should().BeNull();
        agent.State.PendingActions.Should().BeEmpty();
        agent.State.PendingInput.Should().BeNull();
        committed.Should().OnlyContain(item =>
            !Encoding.UTF8.GetString(item.EventData.ToByteArray())
                .Contains(sourceReadableBearer, StringComparison.Ordinal));
        agent.State.ToString().Should().NotContain(sourceReadableBearer);
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
        if (failedStage == "dispatch")
        {
            operations.Should().Equal($"{expectedOperations},dispatch".Split(','),
                "the second dispatch is the exact turn-owned delivery probe");
            agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
            agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
            var uncertainStep = agent.State.ActiveTask.Steps.Should().ContainSingle().Which;
            uncertainStep.Status.Should().Be(NyxIdChatStepStatus.Running);
            uncertainStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Dispatched);
            agent.State.PendingOperationDeliveryProbe.Should().BeEquivalentTo(
                uncertainStep.Operation.Key);
            AssertSingleOperationDeliveryProbe(dispatch, uncertainStep.Operation.Key);
            dispatch.RecoveryCalls.Should().BeEmpty(
                "conversation recovery cannot run before the turn proves delivery admission");

            var uncertainEvents = await eventStore.GetEventsAsync(conversationActorId);
            uncertainEvents.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatOperationDispatchUncertainEvent.Descriptor));
            uncertainEvents.Should().NotContain(item =>
                item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor) &&
                item.EventData.Unpack<NyxIdChatOperationReconciledEvent>().Result != null &&
                item.EventData.Unpack<NyxIdChatOperationReconciledEvent>().Result.Failure != null &&
                item.EventData.Unpack<NyxIdChatOperationReconciledEvent>()
                    .Result.Failure.FailureCode == expectedFailureCode);
            return;
        }

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
    public async Task ActivateAsync_WithPendingReservation_ShouldScheduleCallbacksWithoutSelfDispatch()
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
        callbacks.TimeoutRequests.Clear();
        await FluentActions.Invoking(() => initial.HandleEventAsync(
                CreateEnvelope(conversationActorId, CreateStartTurnCommand())))
            .Should().ThrowAsync<OperationCanceledException>();
        var pending = initial.State.HistoryDeliveryReservation.Clone();
        pending.Dispatched.Should().BeFalse();

        history.ReserveException = null;
        history.Reservations.Clear();
        operations.Clear();
        callbacks.TimeoutRequests.Clear();
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

        operations.Should().BeEmpty();
        recoveryDispatch.Calls.Should().BeEmpty();
        var reservationCallback = callbacks.TimeoutRequests.Should().ContainSingle().Which;
        reservationCallback.TriggerEnvelope.Payload
            .Is(NyxIdChatHistoryDeliveryReservationDispatchRequested.Descriptor).Should().BeTrue();
        reservationCallback.DueTime.Should().BePositive();

        await recovered.HandleEventAsync(reservationCallback.TriggerEnvelope.Clone());

        callbacks.TimeoutRequests.Should().HaveCount(2);
        var recoveryCallback = callbacks.TimeoutRequests.Last();
        recoveryCallback.TriggerEnvelope.Payload
            .Is(NyxIdChatRecoveryRequestedSignal.Descriptor).Should().BeTrue();
        recoveryCallback.DueTime.Should().BePositive();
        operations.Should().Equal("history.reserve");
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
        var recovery = recoveryCallback.TriggerEnvelope.Payload
            .Unpack<NyxIdChatRecoveryRequestedSignal>();
        recovery.Key.OperationId.Should().Be(
            recovered.State.ActiveTask.Steps.Single().Operation.Key.OperationId);
        recovery.ExpectedStateVersion.Should().Be(events[^1].Version);
    }

    [Fact]
    public async Task ActivateAsync_WhenPendingReservationRecoveryFails_ShouldScheduleRetryThenOperationRecovery()
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
        recoveryDispatch.Calls.Should().BeEmpty();
        var reservationCallback = callbacks.TimeoutRequests.Should().ContainSingle().Which;
        reservationCallback.TriggerEnvelope.Payload
            .Is(NyxIdChatHistoryDeliveryReservationDispatchRequested.Descriptor).Should().BeTrue();

        await recovered.HandleEventAsync(reservationCallback.TriggerEnvelope.Clone());

        recovered.State.HistoryDeliveryReservation.Dispatched.Should().BeFalse();
        callbacks.TimeoutRequests.Should().HaveCount(2);
        var retryCallback = callbacks.TimeoutRequests.Last();
        retryCallback.TriggerEnvelope.Payload
            .Is(NyxIdChatHistoryDeliveryReservationDispatchRequested.Descriptor).Should().BeTrue();

        history.ReserveException = null;
        await recovered.HandleEventAsync(retryCallback.TriggerEnvelope.Clone());

        recovered.State.HistoryDeliveryReservation.Dispatched.Should().BeTrue();
        callbacks.TimeoutRequests.Should().HaveCount(3);
        callbacks.TimeoutRequests.Last().TriggerEnvelope.Payload
            .Is(NyxIdChatRecoveryRequestedSignal.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ActivateAsync_WhenRecoverySchedulingFails_ShouldFailActivation()
    {
        const string conversationActorId = "conversation-alpha";
        var history = new RecordingChatHistoryCommandPort([])
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

        callbacks.TimeoutRequests.Clear();
        callbacks.ScheduleException = new InvalidOperationException("scheduler unavailable");
        var recovered = CreateController(services, conversationActorId);

        await FluentActions.Invoking(() => recovered.ActivateAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("scheduler unavailable");
        callbacks.TimeoutRequests.Should().BeEmpty();
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
                var toolKey = agent.State.ActiveTask.Steps
                    .Single(step => step.Kind == NyxIdChatStepKind.Tool).Operation.Key.Clone();
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
        var callbacks = new RecordingRuntimeCallbackScheduler();
        using var services = BuildEventSourcingServices(eventStore, history, callbacks);
        var dispatch = new RecordingActorDispatchPort(
            [],
            static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);

        await agent.ActivateAsync();

        dispatch.Calls.Should().BeEmpty();
        var callback = callbacks.TimeoutRequests.Should().ContainSingle().Which;
        callback.TriggerEnvelope.Propagation.CorrelationId.Should().Be(
            "command-terminal-alpha");
        callback.TriggerEnvelope.Runtime.DeliveryIdentity.OperationId.Should().Be(
            "history-terminal-dispatch-7ab612402d13f08eb287413f5c223404");
        var selfEnvelope = callback.TriggerEnvelope.Clone();
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
        firstDispatch.Calls.Should().BeEmpty();
        var firstSignal = callbacks.TimeoutRequests.Should().ContainSingle().Which
            .TriggerEnvelope.Clone();
        callbacks.TimeoutRequests.Clear();

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
        callbacks.TimeoutRequests.Clear();
        var recovered = CreateController(services, conversationActorId, recoveryDispatch);
        await recovered.ActivateAsync();
        recoveryDispatch.Calls.Should().BeEmpty();
        var recoveredSignal = callbacks.TimeoutRequests.Should().ContainSingle().Which
            .TriggerEnvelope.Clone();
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
        var toolKey = agent.State.ActiveTask.Steps
            .Single(step => step.Kind == NyxIdChatStepKind.Tool).Operation.Key.Clone();
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
        var toolKey = agent.State.ActiveTask.Steps
            .Single(step => step.Kind == NyxIdChatStepKind.Tool).Operation.Key.Clone();

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
            .ContainSingle(step => step.Kind == NyxIdChatStepKind.Postcondition).Which;
        stateObservedAtDispatch.ActiveTask.Steps.Should().Contain(step =>
            step.StepId == "step-tool-alpha");
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
        agent.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition).Operation.Phase.Should().Be(
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
        if (failedStage == "dispatch")
        {
            operations.Should().Equal($"{expectedOperations},dispatch".Split(','),
                "the second dispatch is the exact turn-owned delivery probe");
            agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
            agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
            var uncertainStep = agent.State.ActiveTask.Steps.Should()
                .ContainSingle(candidate => candidate.Kind == NyxIdChatStepKind.Postcondition)
                .Which;
            uncertainStep.Status.Should().Be(NyxIdChatStepStatus.Running);
            uncertainStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Dispatched);
            agent.State.PendingOperationDeliveryProbe.Should().BeEquivalentTo(
                uncertainStep.Operation.Key);
            agent.State.PendingHistoryTerminal.Should().BeNull();
            AssertSingleOperationDeliveryProbe(dispatch, uncertainStep.Operation.Key);
            dispatch.RecoveryCalls.Should().BeEmpty(
                "the action read-back cannot race unknown turn inbox admission");

            var uncertainEvents = await eventStore.GetEventsAsync(conversationActorId);
            uncertainEvents.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatOperationDispatchUncertainEvent.Descriptor));
            uncertainEvents.Should().NotContain(item =>
                item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor) &&
                item.EventData.Unpack<NyxIdChatOperationReconciledEvent>()
                    .Result.Failure.FailureCode == expectedFailureCode);
            return;
        }

        operations.Should().Equal(expectedOperations.Split(','));
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        var step = agent.State.ActiveTask.Steps.Should()
            .ContainSingle(candidate => candidate.Kind == NyxIdChatStepKind.Postcondition).Which;
        agent.State.ActiveTask.Steps.Should().Contain(candidate =>
            candidate.StepId == "step-tool-alpha");
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
        var callbacks = new RecordingRuntimeCallbackScheduler();
        using var services = BuildEventSourcingServices(eventStore, history, callbacks);
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
        callbacks.TimeoutRequests.Clear();
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

        operations.Should().BeEmpty();
        recoveryDispatch.Calls.Should().BeEmpty();
        var reservationCallback = callbacks.TimeoutRequests.Should().ContainSingle().Which;
        reservationCallback.TriggerEnvelope.Payload
            .Is(NyxIdChatHistoryDeliveryReservationDispatchRequested.Descriptor).Should().BeTrue();

        await recovered.HandleEventAsync(reservationCallback.TriggerEnvelope.Clone());

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
        var postconditionKey = agent.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Postcondition).Operation.Key.Clone();

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
    public async Task VerifiedAuthorizationContinuationLlmResult_ShouldApplyAndReplayAcrossOriginTurn()
    {
        const string conversationActorId = "conversation-continuation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var initial = CreateVerifiedAuthorizationContinuationState(conversationActorId);
        await PersistTestStateAsync(eventStore, conversationActorId, 1, initial);
        using var services = BuildEventSourcingServices(eventStore);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var continuationStep = agent.State.ActiveTask.Steps.Single(step =>
            step.Source?.Llm?.ActionRequestId == "action-alpha");

        await agent.HandleOperationResultAsync(new NyxIdChatOperationResultSignal
        {
            Key = continuationStep.Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-read-alpha",
                        ToolName = "nyxop-read-alpha",
                        ArgumentsJson = "{}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            IsReadOnly = true,
                            MayChangeExternalState = false,
                        },
                        NyxIdProvenance = new NyxIdOperationRef
                        {
                            ConnectedServiceId = "us-alpha",
                            ServiceSlug = "service-alpha",
                            OperationId = "endpoint-read-alpha",
                        },
                        OperationAdmission = CreateConnectedServiceReadAdmission(
                            "us-alpha",
                            "service-alpha"),
                    },
                },
            },
        });

        agent.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.Tool &&
            step.Source.Tool.ToolName == "nyxop-read-alpha");
        agent.State.ActiveTask.Steps.Single(step =>
                step.Source?.Llm?.ActionRequestId == "action-alpha")
            .Status.Should().Be(NyxIdChatStepStatus.Done);
        agent.State.ActiveTask.ActiveStepId.Should().NotBe(continuationStep.StepId);

        var recovered = CreateController(services, conversationActorId);
        await recovered.ActivateAsync();

        recovered.State.Should().BeEquivalentTo(agent.State);
        recovered.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.Tool &&
            step.Source.Tool.ToolName == "nyxop-read-alpha");
    }

    [Fact]
    public async Task VerifiedAuthorizationContinuationToolResult_ShouldApplyAndReplayAcrossOriginTurn()
    {
        const string conversationActorId = "conversation-continuation-tool-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var initial = CreateVerifiedAuthorizationContinuationState(conversationActorId);
        await PersistTestStateAsync(eventStore, conversationActorId, 1, initial);
        using var services = BuildEventSourcingServices(eventStore);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var continuationStep = agent.State.ActiveTask.Steps.Single(step =>
            step.Source?.Llm?.ActionRequestId == "action-alpha");
        await agent.HandleOperationResultAsync(new NyxIdChatOperationResultSignal
        {
            Key = continuationStep.Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-read-alpha",
                        ToolName = "nyxop-read-alpha",
                        ArgumentsJson = "{}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            IsReadOnly = true,
                            MayChangeExternalState = false,
                        },
                        NyxIdProvenance = new NyxIdOperationRef
                        {
                            ConnectedServiceId = "us-alpha",
                            ServiceSlug = "service-alpha",
                            OperationId = "endpoint-read-alpha",
                        },
                        OperationAdmission = CreateConnectedServiceReadAdmission(
                            "us-alpha",
                            "service-alpha"),
                    },
                },
            },
        });
        var toolStep = agent.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool &&
            step.Source.Tool.ToolName == "nyxop-read-alpha");
        var beforeToolResult = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleOperationResultAsync(new NyxIdChatOperationResultSignal
        {
            Key = toolStep.Operation.Key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ResultJson = "{\"items\":[{\"id\":\"item-alpha\"}]}",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-read-alpha",
                    ToolName = "nyxop-read-alpha",
                    Status = AgentToolReceiptStatus.Success,
                },
            },
        });

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Skip(beforeToolResult.Count).Should().ContainSingle(item =>
            item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor));
        agent.State.ActiveTask.Steps.Single(step => step.StepId == toolStep.StepId)
            .Status.Should().Be(NyxIdChatStepStatus.Done);
        agent.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.Llm &&
            step.Status == NyxIdChatStepStatus.Running &&
            step.DependsOn.Contains(toolStep.StepId));

        var recovered = CreateController(services, conversationActorId);
        await recovered.ActivateAsync();

        recovered.State.Should().BeEquivalentTo(agent.State);
        recovered.State.ActiveTask.Steps.Single(step => step.StepId == toolStep.StepId)
            .Status.Should().Be(NyxIdChatStepStatus.Done);
    }

    [Fact]
    public async Task VerifiedAuthorizationContinuationAuthorizationRequired_ShouldCommitTypedFailureWithoutNewAction()
    {
        const string conversationActorId = "conversation-continuation-authorization-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var initial = CreateVerifiedAuthorizationContinuationState(conversationActorId);
        await PersistTestStateAsync(eventStore, conversationActorId, 1, initial);
        using var services = BuildEventSourcingServices(eventStore);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var continuationStep = agent.State.ActiveTask.Steps.Single(step =>
            step.Source?.Llm?.ActionRequestId == "action-alpha");
        await agent.HandleOperationResultAsync(new NyxIdChatOperationResultSignal
        {
            Key = continuationStep.Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-read-alpha",
                        ToolName = "nyxop-read-alpha",
                        ArgumentsJson = "{}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            IsReadOnly = true,
                            MayChangeExternalState = false,
                        },
                        NyxIdProvenance = new NyxIdOperationRef
                        {
                            ConnectedServiceId = "us-alpha",
                            ServiceSlug = "service-alpha",
                            OperationId = "endpoint-read-alpha",
                        },
                        OperationAdmission = CreateConnectedServiceReadAdmission(
                            "us-alpha",
                            "service-alpha"),
                    },
                },
            },
        });
        var toolStep = agent.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool &&
            step.Source.Tool.ToolName == "nyxop-read-alpha");
        NyxIdChatActionContinuationCorrelation.TryMatch(
                agent.State,
                agent.State.ActiveTask,
                agent.State.ActiveTurn,
                toolStep.Operation.Key,
                out _)
            .Should().BeTrue(
                "the admitted dynamic tool step must retain the verified action ancestry");
        var authorizationRequiredSignal = new NyxIdChatOperationResultSignal
        {
            Key = toolStep.Operation.Key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-read-alpha",
                    ToolName = "nyxop-read-alpha",
                    Status = AgentToolReceiptStatus.AuthorizationRequired,
                    AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
                    {
                        ServiceSlug = "service-alpha",
                        UserServiceId = "us-alpha",
                        ReasonCode = "USER_SERVICE_ACCESS_REQUIRED",
                        SafeMessage = "The exact service is unavailable to this execution bearer.",
                    },
                },
            },
        };
        var expectedFailure = new NyxIdChatOperationResultSignal
        {
            Key = toolStep.Operation.Key.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = NyxIdChatTurnOperationExecutor
                    .AuthorizationContinuationCapabilityUnavailableCode,
                SafeMessage = NyxIdChatTurnOperationExecutor
                    .AuthorizationContinuationCapabilityUnavailableMessage,
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        };
        var expectedDecision = NyxIdChatTaskLifecycle.ApplyOperationResult(
            agent.State,
            expectedFailure,
            Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero)));
        var lateEvidence = NyxIdChatControlCommands.ReconcileLateOperationEvidence(
            agent.State,
            authorizationRequiredSignal,
            Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero)));
        lateEvidence.IsFencedOperation.Should().BeFalse(
            "the verified action continuation is the active operation, not late control evidence");
        expectedDecision.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        expectedDecision.State.ActiveTurn.Status.Should().Be(
            NyxIdChatTurnStatus.Failed,
            "the verified continuation cannot recover by retrying the same unavailable capability");
        NyxIdChatActionContinuationCorrelation.TryMatch(
                agent.State,
                expectedDecision.State.ActiveTask,
                expectedDecision.State.ActiveTurn,
                toolStep.Operation.Key,
                out _)
            .Should().BeTrue(
                "the terminal transition must remain correlated for authoritative commit");
        var beforeResult = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleOperationResultAsync(authorizationRequiredSignal);

        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        agent.State.ActiveTurn.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        agent.State.PendingActions.Should().BeEmpty(
            "a verified continuation must not request the same browser action again");
        agent.State.RecentActions.Should().ContainSingle(action =>
            action.ActionRequestId == "action-alpha");
        agent.State.ActiveTask.Steps.Single(step => step.StepId == toolStep.StepId)
            .Status.Should().Be(NyxIdChatStepStatus.Failed);
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Skip(beforeResult.Count).Should().ContainSingle(item =>
            item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor) &&
            item.EventData.Unpack<NyxIdChatOperationReconciledEvent>()
                .State.ActiveTurn.FailureCode ==
            NyxIdChatTurnOperationExecutor.AuthorizationContinuationCapabilityUnavailableCode);
        dispatch.Calls.Should().Contain(call => call.Envelope.Payload.Is(
            NyxIdChatTurnOperationResultAcknowledgedSignal.Descriptor));
    }

    [Theory]
    [InlineData("postcondition_not_verified")]
    [InlineData("dependency_cycle")]
    public async Task VerifiedAuthorizationContinuationToolResult_InvalidAncestor_ShouldNotPersist(
        string invalidity)
    {
        var conversationActorId = $"conversation-continuation-invalid-{invalidity}";
        var eventStore = new InMemoryEventStoreForTests();
        var initial = CreateVerifiedAuthorizationContinuationState(conversationActorId);
        await PersistTestStateAsync(eventStore, conversationActorId, 1, initial);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(
            services,
            conversationActorId,
            new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask));
        await agent.ActivateAsync();
        var continuationStep = agent.State.ActiveTask.Steps.Single(step =>
            step.Source?.Llm?.ActionRequestId == "action-alpha");
        await agent.HandleOperationResultAsync(new NyxIdChatOperationResultSignal
        {
            Key = continuationStep.Operation.Key.Clone(),
            Llm = new NyxIdChatLLMOperationResult
            {
                ToolCalls =
                {
                    new NyxIdChatToolCall
                    {
                        CallId = "call-read-alpha",
                        ToolName = "nyxop-read-alpha",
                        ArgumentsJson = "{}",
                        Safety = new NyxIdChatToolCallSafety
                        {
                            IsReadOnly = true,
                            MayChangeExternalState = false,
                        },
                        NyxIdProvenance = new NyxIdOperationRef
                        {
                            ConnectedServiceId = "us-alpha",
                            ServiceSlug = "service-alpha",
                            OperationId = "endpoint-read-alpha",
                        },
                        OperationAdmission = CreateConnectedServiceReadAdmission(
                            "us-alpha",
                            "service-alpha"),
                    },
                },
            },
        });
        var toolStep = agent.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool &&
            step.Source.Tool.ToolName == "nyxop-read-alpha");
        switch (invalidity)
        {
            case "postcondition_not_verified":
                agent.State.ActiveTask.Steps.Single(step =>
                        step.Kind == NyxIdChatStepKind.Postcondition)
                    .ExternalEffect = NyxIdChatEffectEvidence.NotApplied;
                break;
            case "dependency_cycle":
                agent.State.ActiveTask.Steps.Single(step =>
                        step.Source?.Llm?.ActionRequestId == "action-alpha")
                    .DependsOn.Add(toolStep.StepId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidity));
        }
        var beforeToolResult = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleOperationResultAsync(new NyxIdChatOperationResultSignal
        {
            Key = toolStep.Operation.Key.Clone(),
            Tool = new NyxIdChatToolOperationResult
            {
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                Receipt = new AgentToolReceipt
                {
                    CallId = "call-read-alpha",
                    ToolName = "nyxop-read-alpha",
                    Status = AgentToolReceiptStatus.Success,
                },
            },
        });

        (await eventStore.GetEventsAsync(conversationActorId)).Should()
            .HaveCount(beforeToolResult.Count);
        agent.State.ActiveTask.Steps.Single(step => step.StepId == toolStep.StepId)
            .Status.Should().Be(NyxIdChatStepStatus.Running);
    }

    [Fact]
    public async Task ActorOwnedPostconditionWithoutPendingBrowserAction_ShouldUseTaskLifecycle()
    {
        const string conversationActorId = "conversation-actor-owned-postcondition";
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = conversationActorId,
            TurnId = "turn-service-connect",
            TaskId = "task-service-connect",
            StepId = "step-service-connected",
            OperationId = "operation-service-connected",
            OperationGeneration = 1,
        };
        var state = CreateActorOwnedPostconditionState(conversationActorId, key);
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, conversationActorId, 1, state);
        var dispatch = new RecordingActorDispatchPort(
            [],
            static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var before = (await eventStore.GetEventsAsync(conversationActorId)).Count;

        var result = new NyxIdChatOperationResultSignal
        {
            Key = key,
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = "actor-owned-service-connected",
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = "user-service-alpha",
                    },
                },
            },
        };
        await agent.HandleOperationResultAsync(result);

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(before + 1);
        committed[^1].EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor).Should().BeTrue();
        var firstAcknowledgement = dispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(
                    NyxIdChatTurnOperationResultAcknowledgedSignal.Descriptor))
            .Which.Envelope.Payload.Unpack<NyxIdChatTurnOperationResultAcknowledgedSignal>();
        firstAcknowledgement.Key.Should().BeEquivalentTo(key);
        agent.State.PendingActions.Should().BeEmpty();
        agent.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Done);
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should()
            .Be(NyxIdChatOperationPhase.Succeeded);

        await agent.HandleOperationResultAsync(result.Clone());

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(before + 1,
            "an exact redelivery must not reconcile twice");
        dispatch.Calls.Count(call => call.Envelope.Payload.Is(
            NyxIdChatTurnOperationResultAcknowledgedSignal.Descriptor)).Should().Be(2,
            "an exact redelivery still needs an acknowledgement");

        var nextTurn = CreateStartTurnCommand();
        nextTurn.ConversationActorId = conversationActorId;
        nextTurn.TurnId = "turn-after-service-connect";
        nextTurn.TaskId = "task-after-service-connect";
        nextTurn.ClientRequestId = "client-after-service-connect";
        nextTurn.CommandId = "command-after-service-connect";
        nextTurn.CorrelationId = "correlation-after-service-connect";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, nextTurn));
        agent.State.ActiveTurn.TurnId.Should().Be(nextTurn.TurnId);
        agent.State.ResultAcknowledgementFences.Should().ContainSingle(fence =>
            fence.Key.Equals(key));
        var afterNextTurn = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleOperationResultAsync(result.Clone());

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            afterNextTurn.Count,
            "an old child replay after the next turn only retransmits its durable acknowledgement");
        var replayAcknowledgements = dispatch.Calls
            .Where(call => call.Envelope.Payload.Is(
                NyxIdChatTurnOperationResultAcknowledgedSignal.Descriptor))
            .Select(call => call.Envelope.Payload
                .Unpack<NyxIdChatTurnOperationResultAcknowledgedSignal>())
            .ToArray();
        replayAcknowledgements.Should().HaveCount(3);
        replayAcknowledgements[^1].ResultSha256.ToByteArray().Should()
            .Equal(SHA256.HashData(result.ToByteArray()));

        var nextGeneration = result.Clone();
        nextGeneration.Key.OperationGeneration++;
        await agent.HandleOperationResultAsync(nextGeneration);

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(afterNextTurn.Count);
        dispatch.Calls.Count(call => call.Envelope.Payload.Is(
            NyxIdChatTurnOperationResultAcknowledgedSignal.Descriptor)).Should().Be(3,
            "the same operation id from another generation must not match the committed fence");
    }

    [Theory]
    [InlineData(false, "user-service-alpha", NyxIdChatTaskTransitionPolicy.PostconditionNotVerified)]
    [InlineData(true, "user-service-other", NyxIdChatTaskLifecycle.ServiceConnectPostconditionEvidenceMismatch)]
    public async Task RejectedActorOwnedPostcondition_ShouldCommitTypedFailureAndAcknowledgeOriginalResult(
        bool verified,
        string actualUserServiceId,
        string expectedFailureCode)
    {
        const string conversationActorId = "conversation-rejected-postcondition";
        var key = new NyxIdChatOperationKey
        {
            ConversationActorId = conversationActorId,
            TurnId = "turn-service-connect",
            TaskId = "task-service-connect",
            StepId = "step-service-connected",
            OperationId = "operation-service-connected",
            OperationGeneration = 1,
        };
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(
            eventStore,
            conversationActorId,
            1,
            CreateActorOwnedPostconditionState(conversationActorId, key));
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var before = (await eventStore.GetEventsAsync(conversationActorId)).Count;
        var originalResult = new NyxIdChatOperationResultSignal
        {
            Key = key.Clone(),
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = "actor-owned-service-connected",
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = verified,
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = actualUserServiceId,
                    },
                },
            },
        };

        await agent.HandleOperationResultAsync(originalResult);

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(before + 1);
        var reconciled = committed[^1].EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        reconciled.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        reconciled.Result.Failure.FailureCode.Should().Be(expectedFailureCode);
        reconciled.Task.Status.Should().NotBe(NyxIdChatTaskStatus.Succeeded);
        reconciled.Turn.Status.Should().NotBe(NyxIdChatTurnStatus.Succeeded);
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should()
            .NotBe(NyxIdChatOperationPhase.Succeeded);
        var expectedDigest = SHA256.HashData(originalResult.ToByteArray());
        agent.State.ResultAcknowledgementFences.Should().ContainSingle(fence =>
            fence.Key.Equals(key) && fence.ResultSha256.ToByteArray().SequenceEqual(expectedDigest));
        var acknowledgement = dispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(
                    NyxIdChatTurnOperationResultAcknowledgedSignal.Descriptor))
            .Which.Envelope.Payload.Unpack<NyxIdChatTurnOperationResultAcknowledgedSignal>();
        acknowledgement.Key.Should().BeEquivalentTo(key);
        acknowledgement.ResultSha256.ToByteArray().Should().Equal(expectedDigest);
    }

    [Fact]
    public async Task ResultAcknowledgementFence_WithMoreThanSixtyFourReceipts_ShouldReAckOldestExactResult()
    {
        const string conversationActorId = "conversation-receipt-retention";
        var oldestKey = new NyxIdChatOperationKey
        {
            ConversationActorId = conversationActorId,
            TurnId = "turn-0",
            TaskId = "task-0",
            StepId = "step-0",
            OperationId = "operation-0",
            OperationGeneration = 1,
        };
        var oldestResult = new NyxIdChatOperationResultSignal
        {
            Key = oldestKey.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = "POSTCONDITION_FAILED",
                SafeMessage = "The postcondition failed.",
                ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            },
        };
        var state = CreateActorOwnedPostconditionState(conversationActorId, oldestKey);
        for (var index = 0; index < 65; index++)
        {
            var key = oldestKey.Clone();
            key.TurnId = $"turn-{index}";
            key.TaskId = $"task-{index}";
            key.StepId = $"step-{index}";
            key.OperationId = $"operation-{index}";
            var result = oldestResult.Clone();
            result.Key = key;
            state.ResultAcknowledgementFences.Add(
                new NyxIdChatOperationResultAcknowledgementFence
                {
                    Key = key.Clone(),
                    ResultSha256 = ByteString.CopyFrom(SHA256.HashData(result.ToByteArray())),
                });
        }
        var eventStore = new InMemoryEventStoreForTests();
        await PersistTestStateAsync(eventStore, conversationActorId, 1, state);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var before = (await eventStore.GetEventsAsync(conversationActorId)).Count;

        await agent.HandleOperationResultAsync(oldestResult);

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(before);
        agent.State.ResultAcknowledgementFences.Should().HaveCount(65);
        var acknowledgement = dispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(
                    NyxIdChatTurnOperationResultAcknowledgedSignal.Descriptor))
            .Which.Envelope.Payload.Unpack<NyxIdChatTurnOperationResultAcknowledgedSignal>();
        acknowledgement.Key.Should().BeEquivalentTo(oldestKey);
        acknowledgement.ResultSha256.ToByteArray().Should()
            .Equal(SHA256.HashData(oldestResult.ToByteArray()));
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
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            async (_, envelope) =>
            {
                if (!envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor))
                    return;

                var command = envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>();
                if (command.InputCase !=
                    NyxIdChatOperationDispatchCommand.InputOneofCase.Tool)
                {
                    return;
                }

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
                        NyxIdProvenance = new NyxIdOperationRef
                        {
                            ConnectedServiceId = "connected-service-alpha",
                            ServiceSlug = "service-slug-alpha",
                            CatalogServiceSlug = "catalog-slug-alpha",
                            OperationId = "endpoint-alpha",
                            ReadinessCapabilityId = "readiness-capability-alpha",
                        },
                        OperationAdmission = new AgentToolOperationAdmissionPayload
                        {
                            ServiceInstanceId = "connected-service-alpha",
                            ServiceSlug = "service-slug-alpha",
                            PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
                            {
                                EndpointId = "endpoint-alpha",
                            },
                            AuthorizationBasis =
                                AgentToolOperationAuthorizationBasisPayload.PublishedContract,
                            HttpMethod = "PATCH",
                            PathTemplate = "/repositories/{repositoryId}",
                            ContractDigest = new string('b', 64),
                            CatalogDigest = $"sha256:{new string('a', 64)}",
                            ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
                            {
                                Risk = AgentToolOperationRiskPayload.Write,
                                Approval = AgentToolOperationApprovalPayload.Required,
                                EnforcementOwner =
                                    AgentToolOperationEnforcementOwnerPayload.Aevatar,
                                AllowedExecutionModes =
                                {
                                    AgentToolOperationExecutionModePayload.Interactive,
                                },
                            },
                        },
                        Presentation = ToolPresentationDescriptors.Skill(
                            "repository_update",
                            "Repository maintenance",
                            "Update the exact repository.",
                            "repository-maintenance",
                            "remote"),
                    },
                },
            },
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, llmResult));
        var afterLlmResult = await eventStore.GetEventsAsync(conversationActorId);
        var llmReconciliation = afterLlmResult.Last(item =>
                item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .EventData
            .Unpack<NyxIdChatOperationReconciledEvent>();
        var committedCall = llmReconciliation.Result.Llm.ToolCalls.Should()
            .ContainSingle().Which;
        committedCall.ArgumentsJson.Should().BeEmpty(
            "tool arguments are transient dispatch input, not durable product facts");
        committedCall.NyxIdProvenance.ConnectedServiceId.Should().Be("connected-service-alpha");
        committedCall.NyxIdProvenance.ServiceSlug.Should().Be("service-slug-alpha");
        committedCall.NyxIdProvenance.CatalogServiceSlug.Should().Be("catalog-slug-alpha");
        committedCall.NyxIdProvenance.ReadinessCapabilityId.Should()
            .Be("readiness-capability-alpha");
        committedCall.Presentation.Kind.Should().Be(ToolPresentationKind.Skill);
        committedCall.Presentation.Skill.SkillName.Should().Be("repository-maintenance");

        dispatch.OperationCalls.Should().HaveCount(2);
        var successor = dispatch.OperationCalls[^1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        stateObservedAtSuccessorDispatch.Should().NotBeNull();
        var toolStep = stateObservedAtSuccessorDispatch!.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        toolStep.Kind.Should().Be(NyxIdChatStepKind.Tool);
        toolStep.Status.Should().Be(NyxIdChatStepStatus.Running);
        toolStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Requested);
        eventsObservedAtSuccessorDispatch.Should().NotBeNull();
        eventsObservedAtSuccessorDispatch![^1].EventData.Is(
            NyxIdChatOperationReconciledEvent.Descriptor).Should().BeTrue();
        var committedReconciliation = eventsObservedAtSuccessorDispatch[^1].EventData
            .Unpack<NyxIdChatOperationReconciledEvent>();
        committedReconciliation.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool).Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Requested);
        toolStep.Source.Tool.ReadinessCapabilityId.Should().Be("readiness-capability-alpha");
        toolStep.Source.Tool.Presentation.Skill.SkillName.Should()
            .Be("repository-maintenance");
        successor.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        successor.Tool.ArgumentsJson.Should().Be("{\"repositoryId\":\"repo-alpha\"}");
        successor.Tool.OperationAdmission.ExecutionPolicy.Risk.Should().Be(
            AgentToolOperationRiskPayload.Write);
        successor.Tool.OperationAdmission.ExecutionPolicy.Approval.Should().Be(
            AgentToolOperationApprovalPayload.Required);
        successor.Tool.Presentation.Skill.SkillName.Should()
            .Be("repository-maintenance");
        eventsObservedAtSuccessorDispatch.Should().HaveCount(afterLlmResult.Count - 1,
            "the reconciliation waterline commits before the direct tool dispatch");
        afterLlmResult[^1].EventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor)
            .Should().BeTrue();
        agent.State.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool)
            .Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);

        var reactivated = CreateController(services, conversationActorId);
        await reactivated.ActivateAsync();
        var reactivatedSource = reactivated.State.ActiveTask.Steps
            .Single(step => step.Kind == NyxIdChatStepKind.Tool).Source.Tool;
        reactivatedSource.ServiceId.Should().Be("connected-service-alpha");
        reactivatedSource.ServiceSlug.Should().Be("service-slug-alpha");
        reactivatedSource.ReadinessCapabilityId.Should().Be("readiness-capability-alpha");
        reactivatedSource.Presentation.Skill.SkillName.Should().Be("repository-maintenance");
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
    public async Task ProgressQueuedBeforeStopButDeliveredAfterFence_ShouldNotCommit()
    {
        const string actorId = "conversation-late-progress";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId);
        await agent.ActivateAsync();
        var start = CreateStartTurnCommand();
        start.ConversationActorId = actorId;
        await agent.HandleEventAsync(CreateEnvelope(actorId, start));
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var version = (await eventStore.GetEventsAsync(actorId))[^1].Version;

        var stop = CreateStopCommand(version);
        stop.ConversationActorId = actorId;
        await agent.HandleEventAsync(CreateEnvelope(actorId, stop));
        var afterFence = await eventStore.GetEventsAsync(actorId);
        var stoppedProgressSequence = agent.State.ProgressSequence;

        await agent.HandleOperationProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key,
            Sequence = 1,
            Text = new NyxIdChatTextProgress { Delta = "must-not-commit" },
        });

        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(afterFence.Count);
        agent.State.ProgressSequence.Should().Be(stoppedProgressSequence);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
    }

    [Fact]
    public async Task ChildPhaseProgress_ShouldCommitPresentationOnlySubstepsAndSubstepFrames()
    {
        const string actorId = "conversation-phase-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, actorId);
        await agent.ActivateAsync();
        var start = CreateStartTurnCommand();
        start.ConversationActorId = actorId;
        await agent.HandleEventAsync(CreateEnvelope(actorId, start));
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();

        await agent.HandleOperationProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key.Clone(),
            Sequence = 1,
            Phase = new NyxIdChatOperationPhaseProgress
            {
                SubstepId = "execute-operation",
                Title = "Execute operation",
                Status = NyxIdChatSubstepStatus.Running,
            },
        });

        var committed = await eventStore.GetEventsAsync(actorId);
        var progressed = committed[^1].EventData.Unpack<NyxIdChatOperationProgressedEvent>();
        progressed.StepChangeKind.Should().Be(NyxIdChatStepChangeKind.Substep);
        var step = agent.State.ActiveTask.Steps.Single();
        step.Substeps.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new NyxIdChatSubstepState
            {
                SubstepId = "execute-operation",
                Title = "Execute operation",
                Status = NyxIdChatSubstepStatus.Running,
            });
        progressed.State.ActiveTask.Should().BeEquivalentTo(agent.State.ActiveTask);
        var frames = NyxIdChatConversationAguiFrameBuilder.BuildProgressed("turn-alpha", progressed);
        frames.Should().HaveCount(2);
        frames[1].Custom.Payload.Unpack<NyxIdChatTaskStepChanged>()
            .ChangeKind.Should().Be(NyxIdChatStepChangeKind.Substep);

        await agent.HandleOperationProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key.Clone(),
            Sequence = 2,
            Phase = new NyxIdChatOperationPhaseProgress
            {
                SubstepId = "hidden-external-call",
                Title = "Hidden external call",
                Status = NyxIdChatSubstepStatus.Done,
            },
        });
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(committed.Count,
            "a phase cannot appear terminal without an actor-observed running phase");

        await agent.HandleOperationProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = key,
            Sequence = 2,
            Phase = new NyxIdChatOperationPhaseProgress
            {
                SubstepId = "execute-operation",
                Title = "Execute operation",
                Status = NyxIdChatSubstepStatus.Done,
            },
        });
        agent.State.ActiveTask.Steps.Single().Substeps.Single().Status.Should()
            .Be(NyxIdChatSubstepStatus.Done);
    }

    [Fact]
    public async Task GenuineProgress_ShouldEmitBoundedThirtySecondStepChangesThenExposeSilence()
    {
        const string actorId = "conversation-cadence-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var startedAt = new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(startedAt);
        using var services = BuildEventSourcingServices(eventStore, callbackScheduler: scheduler);
        var agent = CreateController(services, actorId, timeProvider: clock);
        await agent.ActivateAsync();
        var start = CreateStartTurnCommand();
        start.ConversationActorId = actorId;
        await agent.HandleEventAsync(CreateEnvelope(actorId, start));
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();

        async Task ReportAsync(long sequence, string delta)
        {
            await agent.HandleOperationProgressAsync(new NyxIdChatOperationProgressSignal
            {
                Key = key.Clone(),
                Sequence = sequence,
                Text = new NyxIdChatTextProgress { Delta = delta },
            });
        }

        await ReportAsync(1, "first genuine progress");
        var first = (await eventStore.GetEventsAsync(actorId))[^1]
            .EventData.Unpack<NyxIdChatOperationProgressedEvent>();
        first.StepChangeKind.Should().Be(NyxIdChatStepChangeKind.Status);
        NyxIdChatConversationAguiFrameBuilder.BuildProgressed(key.TurnId, first)
            .Should().HaveCount(3, "text plus one bounded task/step heartbeat is published");

        clock.Advance(TimeSpan.FromSeconds(1));
        await ReportAsync(2, "coalesced progress one");
        clock.Advance(TimeSpan.FromSeconds(19));
        await ReportAsync(3, "coalesced progress two");

        var progressEvents = (await eventStore.GetEventsAsync(actorId))
            .Where(entry => entry.EventData.Is(NyxIdChatOperationProgressedEvent.Descriptor))
            .Select(entry => entry.EventData.Unpack<NyxIdChatOperationProgressedEvent>())
            .ToArray();
        progressEvents[^2].StepChangeKind.Should().Be(NyxIdChatStepChangeKind.Unspecified);
        progressEvents[^1].StepChangeKind.Should().Be(NyxIdChatStepChangeKind.Unspecified);
        scheduler.TimeoutRequests.Count(request =>
                request.TriggerEnvelope.Payload.Is(
                    NyxIdChatOperationStepChangedDueSignal.Descriptor))
            .Should().Be(1, "many token deltas share one durable cadence flush");

        async Task FlushCadenceAtAsync(int targetSecond)
        {
            clock.Advance(startedAt.AddSeconds(targetSecond) - clock.GetUtcNow());
            var due = scheduler.TimeoutRequests.Last(request =>
                    request.TriggerEnvelope.Payload.Is(
                        NyxIdChatOperationStepChangedDueSignal.Descriptor))
                .TriggerEnvelope.Payload.Unpack<NyxIdChatOperationStepChangedDueSignal>();
            await agent.HandleOperationStepChangedDueAsync(due);
            var committed = (await eventStore.GetEventsAsync(actorId))[^1]
                .EventData.Unpack<NyxIdChatOperationStepChangedCommittedEvent>();
            committed.GenuineProgressSequence.Should()
                .Be(agent.State.ActiveTask.Steps.Single().Operation.LatestProgressSequence);
            var frame = NyxIdChatConversationAguiFrameBuilder.BuildProgressCadence(committed)[1]
                .Custom.Payload.Unpack<NyxIdChatTaskStepChanged>();
            frame.ChangeKind.Should().Be(NyxIdChatStepChangeKind.Status);
        }

        await FlushCadenceAtAsync(30);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ReportAsync(4, "window two progress");
        clock.Advance(TimeSpan.FromSeconds(19));
        await ReportAsync(5, "window two latest");
        await FlushCadenceAtAsync(60);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ReportAsync(6, "window three progress");
        clock.Advance(TimeSpan.FromSeconds(19));
        await ReportAsync(7, "window three latest");
        await FlushCadenceAtAsync(90);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ReportAsync(8, "window four progress");
        clock.Advance(TimeSpan.FromSeconds(28));
        await ReportAsync(9, "window four latest");
        await FlushCadenceAtAsync(120);

        var noProgressDue = scheduler.TimeoutRequests.Last(request =>
                request.TriggerEnvelope.Payload.Is(
                    NyxIdChatOperationStepChangedDueSignal.Descriptor))
            .TriggerEnvelope.Payload.Unpack<NyxIdChatOperationStepChangedDueSignal>();
        var beforeSilentCadence = (await eventStore.GetEventsAsync(actorId)).Count;
        await agent.HandleOperationStepChangedDueAsync(noProgressDue);
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(beforeSilentCadence,
            "a timer without newer genuine executor progress cannot fabricate step.changed");

        var originalStall = scheduler.TimeoutRequests.First(request =>
                request.TriggerEnvelope.Payload.Is(NyxIdChatOperationStallCheckSignal.Descriptor))
            .TriggerEnvelope.Payload.Unpack<NyxIdChatOperationStallCheckSignal>();
        await agent.HandleOperationStallCheckAsync(originalStall);
        agent.State.Attention.AttentionKind.Should().Be(NyxIdChatAttentionKind.None,
            "continued genuine progress makes the original stall timer stale");

        clock.Advance(TimeSpan.FromSeconds(119));
        var silenceDeadline = scheduler.TimeoutRequests.Last(request =>
                request.TriggerEnvelope.Payload.Is(NyxIdChatOperationStallCheckSignal.Descriptor))
            .TriggerEnvelope.Payload.Unpack<NyxIdChatOperationStallCheckSignal>();
        await agent.HandleOperationStallCheckAsync(silenceDeadline);
        agent.State.Attention.AttentionKind.Should().Be(NyxIdChatAttentionKind.Stalled);
        var stalled = (await eventStore.GetEventsAsync(actorId))[^1]
            .EventData.Unpack<NyxIdChatOperationStalledEvent>();
        NyxIdChatConversationAguiFrameBuilder.BuildStalled(stalled)[1]
            .Custom.Payload.Unpack<NyxIdChatTaskStepChanged>()
            .ChangeKind.Should().Be(NyxIdChatStepChangeKind.Status);
    }

    [Fact]
    public async Task StallCheck_ShouldCommitOnlyAfterActorDeadlineAndSurviveReload()
    {
        const string actorId = "conversation-stall-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        using var services = BuildEventSourcingServices(eventStore, callbackScheduler: scheduler);
        var agent = CreateController(services, actorId, timeProvider: clock);
        await agent.ActivateAsync();
        var start = CreateStartTurnCommand();
        start.ConversationActorId = actorId;
        await agent.HandleEventAsync(CreateEnvelope(actorId, start));
        var operation = agent.State.ActiveTask.Steps.Single().Operation;
        var signal = new NyxIdChatOperationStallCheckSignal
        {
            Key = operation.Key.Clone(),
            ExpectedProgressSequence = operation.LatestProgressSequence,
            ExpectedLastProgressAt = operation.LastProgressAt.Clone(),
        };
        var before = (await eventStore.GetEventsAsync(actorId)).Count;

        clock.Advance(TimeSpan.FromSeconds(119));
        await agent.HandleOperationStallCheckAsync(signal.Clone());

        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(before);
        agent.State.Attention.AttentionKind.Should().Be(NyxIdChatAttentionKind.None);
        agent.State.ActiveTask.Steps.Single().AvailableActions.Stop.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(2));
        await agent.HandleOperationStallCheckAsync(signal.Clone());

        var stalledEvents = await eventStore.GetEventsAsync(actorId);
        stalledEvents.Should().HaveCount(before + 1);
        stalledEvents[^1].EventData.Is(NyxIdChatOperationStalledEvent.Descriptor).Should().BeTrue();
        agent.State.Attention.AttentionKind.Should().Be(NyxIdChatAttentionKind.Stalled);
        agent.State.Attention.AttentionSince.Should().Be(
            Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 7, 24, 8, 2, 0, TimeSpan.Zero)));
        agent.State.ActiveTask.Steps.Single().AvailableActions.Stop.Should().BeTrue();

        var recovered = CreateController(services, actorId, timeProvider: clock);
        await recovered.ActivateAsync();
        recovered.State.Attention.Should().BeEquivalentTo(agent.State.Attention);
        recovered.State.ActiveTask.Steps.Single().Operation.StalledAt.Should()
            .Be(agent.State.ActiveTask.Steps.Single().Operation.StalledAt);

        await recovered.HandleOperationProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = signal.Key.Clone(),
            Sequence = 1,
            Text = new NyxIdChatTextProgress { Delta = "real progress" },
        });
        recovered.State.Attention.AttentionKind.Should().Be(NyxIdChatAttentionKind.None);
        recovered.State.ActiveTask.Steps.Single().Operation.StalledAt.Should().BeNull();
        var liveProgress = (await eventStore.GetEventsAsync(actorId))[^1]
            .EventData.Unpack<NyxIdChatOperationProgressedEvent>();
        liveProgress.State.Attention.AttentionKind.Should().Be(NyxIdChatAttentionKind.None);
        liveProgress.State.ActiveTask.Steps.Single().Operation.StalledAt.Should().BeNull();

        var reloadedAfterProgress = CreateController(services, actorId, timeProvider: clock);
        await reloadedAfterProgress.ActivateAsync();
        reloadedAfterProgress.State.Attention.AttentionKind.Should()
            .Be(NyxIdChatAttentionKind.None);
        reloadedAfterProgress.State.ActiveTask.Steps.Single().Operation.StalledAt.Should()
            .BeNull();

        var afterRecovery = (await eventStore.GetEventsAsync(actorId)).Count;
        var stale = signal.Clone();
        stale.Key.OperationGeneration++;
        await recovered.HandleOperationStallCheckAsync(stale);
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(afterRecovery);
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
                        NyxIdProvenance = new NyxIdOperationRef
                        {
                            ConnectedServiceId = "svc-repository-alpha",
                            ServiceSlug = "repository-service",
                            OperationId = "repository-update",
                        },
                        OperationAdmission = CreateEffectAdmissionWithReadBack(),
                    },
                },
            },
        }));
        var toolKey = agent.State.ActiveTask.Steps
            .Single(step => step.Kind == NyxIdChatStepKind.Tool).Operation.Key.Clone();
        var beforeStop = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateStopCommand(beforeStop[^1].Version)));

        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        var stoppedToolBeforeReceipt = agent.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        stoppedToolBeforeReceipt.Status.Should().Be(NyxIdChatStepStatus.Uncertain);
        stoppedToolBeforeReceipt.ExternalEffect.Should().Be(
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
        committed.Should().HaveCount(beforeStop.Count + 3);
        committed[^2].EventData.TypeUrl.Should().EndWith(
            "NyxIdChatLateOperationEvidenceCommittedEvent");
        var evidence = committed[^2].EventData.Unpack<NyxIdChatLateOperationEvidenceCommittedEvent>();
        evidence.Key.Should().BeEquivalentTo(toolKey);
        evidence.OperationPhase.Should().Be(NyxIdChatOperationPhase.Uncertain);
        evidence.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        evidence.ToolReceipt.Status.Should().Be(
            Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success);
        evidence.ToString().Should().NotContain("must-not-be-committed");

        var stoppedTool = agent.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        stoppedTool.Status.Should().Be(NyxIdChatStepStatus.Uncertain,
            "late evidence cannot regress or advance the terminal stopped step");
        stoppedTool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.MayHaveChanged);
        stoppedTool.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Uncertain);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Stopped);
        dispatch.OperationCalls.Should().HaveCount(3,
            "late effect success must start only the frozen read-back operation");
        var verification = dispatch.OperationCalls[^1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        verification.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification);
        verification.ToolVerification.EffectStepId.Should().Be(toolKey.StepId);
        verification.ToolVerification.ReadBack.Should().BeEquivalentTo(
            CreateEffectAdmissionWithReadBack().ReadBack);

        var verificationResult = new NyxIdChatToolVerificationResult
        {
            EffectStepId = toolKey.StepId,
            Disposition = NyxIdChatToolVerificationDisposition.Applied,
            ReadOperation = verification.ToolVerification.ReadBack.ReadOperation.Clone(),
            CheckName = verification.ToolVerification.ReadBack.CheckName,
        };
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, new NyxIdChatOperationResultSignal
        {
            Key = verification.Key.Clone(),
            ToolVerification = verificationResult.Clone(),
        }));

        var afterVerification = await eventStore.GetEventsAsync(conversationActorId);
        afterVerification[^1].EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor)
            .Should().BeTrue();
        var reconciliation = afterVerification[^1].EventData
            .Unpack<NyxIdChatOperationReconciledEvent>();
        reconciliation.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification);
        reconciliation.Result.ToolVerification.Should().BeEquivalentTo(verificationResult);
        reconciliation.RefinesExistingTerminal.Should().BeTrue();
        agent.State.ActiveTask.Steps.Single(step => step.StepId == toolKey.StepId)
            .ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);

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

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            afterVerification.Count,
            "terminal exact evidence is monotonic and conflicting duplicates fail closed");
        agent.State.ActiveTask.Steps.Single(step =>
                step.Kind == NyxIdChatStepKind.Tool).ExternalEffect.Should().Be(
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
    public async Task Uc2StoppedTask_ShouldNeverResumeAndDistinctRestartShouldPublishHonestArtifact()
    {
        const string conversationActorId = "conversation-uc2";
        const string artifact = """
            Verified facts:
            - North Olive lists a private room for 6, vegetarian choices, and Friday 7 pm.

            Cannot check right now: Atlas Taverna's Friday hours were not present in the successful search evidence.

            Research artifact only: no reservation was made.
            """;
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var original = CreateStartTurnCommand();
        original.ConversationActorId = conversationActorId;
        original.TurnId = "turn-uc2-2";
        original.TaskId = "task-uc2";
        original.ClientRequestId = "client-uc2-2";
        original.CommandId = "command-uc2-2";
        original.CorrelationId = "correlation-uc2-2";
        original.Prompt = "Refine the dinner shortlist for 7 pm and a private room.";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, original));
        var beforeStop = await eventStore.GetEventsAsync(conversationActorId);
        var stop = CreateStopCommand(beforeStop[^1].Version);
        stop.ConversationActorId = conversationActorId;
        stop.TurnId = original.TurnId;
        stop.StopRequestId = "stop-uc2-1";
        stop.ClientRequestId = "client-stop-uc2-1";
        stop.CommandId = "command-stop-uc2-1";
        stop.CorrelationId = "correlation-stop-uc2-1";

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, stop));

        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Stopped);
        var stoppedTask = agent.State.ActiveTask.Clone();
        var stoppedTerminalDispatch = dispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor))
            .Which.Envelope.Clone();
        await agent.HandleEventAsync(stoppedTerminalDispatch);
        agent.State.PendingHistoryTerminal.Should().BeNull();
        var next = CreateStartTurnCommand();
        next.ConversationActorId = conversationActorId;
        next.TurnId = "turn-uc2b-1";
        next.TaskId = "task-uc2b";
        next.ClientRequestId = "client-uc2b-1";
        next.CommandId = "command-uc2b-1";
        next.CorrelationId = "correlation-uc2b-1";
        next.Prompt = "Finish the research-only dinner shortlist; do not place a reservation.";

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, next));

        agent.State.ActiveTask.TaskId.Should().Be("task-uc2b");
        agent.State.ActiveTask.TaskId.Should().NotBe(stoppedTask.TaskId);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        agent.State.ActiveTask.Steps.Should().NotContain(step =>
            stoppedTask.Steps.Any(oldStep => oldStep.StepId == step.StepId));
        agent.State.RecentTerminalTurns.Should().ContainSingle(summary =>
            summary.TurnId == stoppedTask.TurnId &&
            summary.TaskId == stoppedTask.TaskId &&
            summary.Status == NyxIdChatTurnStatus.Stopped);
        dispatch.OperationCalls.Should().HaveCount(2,
            "the new goal starts one new operation and never redispatches the stopped task");

        var restartKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = restartKey,
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = artifact,
                    FinishReason = "stop",
                },
            }));

        agent.State.ActiveTask.TaskId.Should().Be("task-uc2b");
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        agent.State.PendingHistoryTerminal.Should().NotBeNull();
        agent.State.PendingHistoryTerminal.TurnId.Should().Be("turn-uc2b-1");
        agent.State.PendingHistoryTerminal.Text.Should().Be(artifact)
            .And.Contain("Verified facts:")
            .And.Contain("Cannot check right now:")
            .And.EndWith("Research artifact only: no reservation was made.");
        agent.State.RecentTerminalTurns.Should().Contain(summary =>
            summary.TurnId == "turn-uc2-2" &&
            summary.TaskId == "task-uc2" &&
            summary.Status == NyxIdChatTurnStatus.Stopped);
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
        agent.State.OwnerSubject = "owner-alpha";
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            WithOwner(CreateStartTurnCommand(), "owner-alpha")));
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
            ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
            {
                Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
                {
                    OwnerSubject = "owner-alpha",
                },
            },
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
        committedFence.Fence.StepId.Should().NotBeNullOrWhiteSpace();
        committedFence.Fence.StepId.Should().Be(committedFence.State.ControlFence.StepId);
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
        agent.State.OwnerSubject = "owner-alpha";
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            WithOwner(CreateStartTurnCommand(), "owner-alpha")));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var taskId = agent.State.ActiveTask.TaskId;
        var planRevision = agent.State.ActiveTask.PlanRevision;
        agent.State.ActiveTask.PlanRevisions.Should().ContainSingle().Which.RevisionCause
            .Should().Be(NyxIdChatPlanRevisionCause.Initial);
        agent.State.ActiveTask.PlanRevisionHistoryStart.Should().Be(1);
        agent.State.ActiveTask.Steps.Single().AddedInPlanRevision.Should().Be(1);
        var oldKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var steering = CreateSteeringCommand(afterStart[^1].Version);
        steering.ToolContext.Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
        {
            OwnerSubject = "owner-alpha",
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));

        var continuationTurnId = agent.State.ContinuationAdmission.ContinuationTurnId;
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);
        agent.State.PendingSteeringContinuation.Should().NotBeNull();
        agent.State.PendingSteeringContinuationId.Should().Be(continuationTurnId);
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");
        (await eventStore.GetEventsAsync(conversationActorId))
            .Select(static evt => evt.EventData.ToString())
            .Should().NotContain(value =>
                value.Contains("steering-runtime-token-alpha", StringComparison.Ordinal));
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

        agent.State.ActiveTurn.TurnId.Should().Be("turn-alpha",
            "the continuation must not advance inline in the steering actor turn");
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        dispatch.StartTurnCalls.Should().BeEmpty(
            "the transient continuation command must remain in the vault");
        var selfDispatch = dispatch.Calls.Single(call =>
            call.Envelope.Payload.Is(
                NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor));
        selfDispatch.ActorId.Should().Be(conversationActorId);
        var dispatchSignal = selfDispatch.Envelope.Payload
            .Unpack<NyxIdChatPendingSteeringContinuationDispatchRequested>();
        dispatchSignal.TurnId.Should().Be(continuationTurnId);
        dispatchSignal.CredentialRef.Should().Be(agent.State.PendingSteeringContinuation.Ref);
        selfDispatch.Envelope.ToString().Should().NotContain("steering-runtime-token-alpha");
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");

        await agent.HandleEventAsync(selfDispatch.Envelope);

        agent.State.ActiveTurn.TurnId.Should().Be(continuationTurnId);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        agent.State.ActiveTask.TaskId.Should().Be(taskId);
        agent.State.ActiveTask.PlanRevision.Should().Be(planRevision + 1);
        agent.State.ActiveTask.PlanRevisions.Should().HaveCount(2);
        agent.State.ActiveTask.PlanRevisionHistoryStart.Should().Be(1);
        agent.State.ActiveTask.PlanRevisions[^1].RevisionCause.Should()
            .Be(NyxIdChatPlanRevisionCause.Steering);
        agent.State.ActiveTask.PlanRevisions[^1].AddedStepIds.Should()
            .ContainSingle(stepId => stepId != oldKey.StepId);
        agent.State.ActiveTask.PlanRevisions[^1].CancelledStepIds.Should()
            .ContainSingle().Which.Should().Be(oldKey.StepId);
        agent.State.ActiveTask.Steps.Should().HaveCount(2);
        agent.State.ActiveTask.Steps.Should().Contain(step =>
            step.Operation != null &&
            step.Operation.Key != null &&
            step.Operation.Key.TurnId == "turn-alpha" &&
            step.Status == NyxIdChatStepStatus.Cancelled);
        agent.State.ActiveTask.Steps.Single(step => step.StepId == oldKey.StepId)
            .CancelledInPlanRevision.Should().Be(planRevision + 1);
        agent.State.ActiveTask.Steps.Should().Contain(step =>
            step.Operation != null &&
            step.Operation.Key != null &&
            step.Operation.Key.TurnId == continuationTurnId &&
            step.AddedBy == NyxIdChatStepAddedBy.Steering);
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        agent.State.PendingSteeringContinuation.Should().BeNull();
        agent.State.PendingSteeringContinuationId.Should().BeEmpty();
        agent.State.PendingSteeringContinuationExpiresAt.Should().BeNull();
        dispatch.OperationCalls.Should().HaveCount(2);
        var continuation = dispatch.OperationCalls.Last().Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        continuation.Key.TurnId.Should().Be(continuationTurnId);
        continuation.Llm.Request.Prompt.Should()
            .Contain("Steering instruction: Use the safer read-only approach.")
            .And.Contain("Original task: hello");
        agent.State.ActiveTurn.Prompt.Should().Be("Use the safer read-only approach.",
            "the transcript-visible prompt remains the user's steering instruction");
        continuation.Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        continuation.Llm.Request.ToolContext.Credentials.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");

        await agent.HandleEventAsync(selfDispatch.Envelope.Clone());
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering.Clone()));

        dispatch.OperationCalls.Should().HaveCount(2,
            "duplicate self delivery and exact steering replay cannot restart the continuation");
        var finalized = (await eventStore.GetEventsAsync(conversationActorId))
            .Where(evt => evt.EventData.Is(
                NyxIdChatPendingSteeringContinuationFinalizedEvent.Descriptor))
            .Select(evt => evt.EventData.Unpack<
                NyxIdChatPendingSteeringContinuationFinalizedEvent>())
            .Should().ContainSingle().Which;
        finalized.Outcome.Should().Be(
            NyxIdChatPendingSteeringContinuationOutcome.Started);
        finalized.State.PendingSteeringContinuation.Should().BeNull();
        finalized.State.PendingSteeringContinuationId.Should().BeEmpty();
        finalized.State.PendingSteeringContinuationExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Uc2SteeringRevision_ShouldPreserveCompletedSearchEvidenceAndEmitFullTaskSnapshot()
    {
        const string conversationActorId = "conversation-uc2";
        const string originalTurnId = "turn-uc2-1";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var classifier = new RecordingTurnIntentClassifier(NyxIdChatTurnIntent.Unspecified);
        var agent = CreateController(
            services,
            conversationActorId,
            dispatch,
            turnIntentClassifier: classifier);
        await agent.ActivateAsync();
        agent.State.OwnerSubject = "owner-alpha";
        var start = CreateStartTurnCommand();
        start.ConversationActorId = conversationActorId;
        start.TurnId = originalTurnId;
        start.TaskId = "task-uc2";
        start.ClientRequestId = "client-uc2-1";
        start.CommandId = "command-uc2-1";
        start.CorrelationId = "correlation-uc2-1";
        start.Prompt = "Research Greek dinner options for Friday in northern Singapore.";
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            WithOwner(start, "owner-alpha")));

        var task = agent.State.ActiveTask;
        var superseded = task.Steps.Should().ContainSingle().Which;
        superseded.Order = 3;
        superseded.AddedBy = NyxIdChatStepAddedBy.Replan;
        superseded.AddedInPlanRevision = 2;
        superseded.Description = "Compare the current service state.";
        var completed = CreateCompletedReadOnlySearchStep(
            conversationActorId,
            originalTurnId,
            task.TaskId);
        completed.Order = 2;
        var completedInput = new NyxIdChatTaskStepState
        {
            StepId = "step-uc2-input",
            Order = 1,
            Kind = NyxIdChatStepKind.Input,
            Status = NyxIdChatStepStatus.Done,
            Description = "Resolve dinner logistics and research-only scope.",
            Source = new NyxIdChatStepSource
            {
                Input = new NyxIdChatInputStepSource
                {
                    RequestId = "input-uc2-logistics",
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            AddedBy = NyxIdChatStepAddedBy.Initial,
            AddedInPlanRevision = 1,
            UpdatedAt = completed.UpdatedAt.Clone(),
        };
        var supersededFollowUp = new NyxIdChatTaskStepState
        {
            StepId = "step-uc2-superseded-follow-up",
            Order = 4,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Planned,
            Required = true,
            Description = "Communicate the superseded comparison.",
            Source = new NyxIdChatStepSource
            {
                Llm = new NyxIdChatLLMStepSource(),
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = conversationActorId,
                    TurnId = originalTurnId,
                    TaskId = task.TaskId,
                    StepId = "step-uc2-superseded-follow-up",
                    OperationId = "operation-uc2-superseded-follow-up",
                    OperationGeneration = 1,
                },
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Requested,
                RequestedAt = completed.UpdatedAt.Clone(),
            },
            AddedBy = NyxIdChatStepAddedBy.Replan,
            AddedInPlanRevision = 2,
            UpdatedAt = completed.UpdatedAt.Clone(),
        };
        supersededFollowUp.DependsOn.Add(superseded.StepId);
        var supersededArtifact = supersededFollowUp.Clone();
        supersededArtifact.StepId = "step-uc2-superseded-artifact";
        supersededArtifact.Order = 5;
        supersededArtifact.Description = "Produce the superseded research artifact.";
        supersededArtifact.Operation.Key.StepId = supersededArtifact.StepId;
        supersededArtifact.Operation.Key.OperationId = "operation-uc2-superseded-artifact";
        supersededArtifact.DependsOn.Clear();
        supersededArtifact.DependsOn.Add(supersededFollowUp.StepId);
        task.Steps.Insert(0, completed);
        task.Steps.Insert(0, completedInput);
        task.Steps.Add(supersededArtifact);
        task.Steps.Add(supersededFollowUp);
        const string committedCompositeAnswer =
            "Party size: 4; dietary needs: one vegetarian; budget cap: SGD 200 total; " +
            "research only: do not reserve, contact, or message venues.";
        var committedInputResolution = new NyxIdChatInputResolutionState
        {
            RequestId = "input-uc2-logistics",
            ClientRequestId = "client-input-uc2-logistics",
            Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
            AnswerSha256 = ByteString.CopyFromUtf8("committed-answer-fingerprint"),
            Answer = new NyxIdChatInputAnswer
            {
                FreeText = committedCompositeAnswer,
            },
            CommittedAt = completed.UpdatedAt.Clone(),
        };
        agent.State.RecentInputResolutions.Add(committedInputResolution.Clone());
        agent.State.LatestInputResolution = committedInputResolution.Clone();
        task.PlanRevision = 2;
        task.PlanRevisionHistoryStart = 1;
        task.PlanRevisions.Clear();
        task.PlanRevisions.Add(new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = 1,
            RevisionCause = NyxIdChatPlanRevisionCause.Initial,
            CommittedAt = completed.UpdatedAt.Clone(),
            AddedStepIds = { completed.StepId },
        });
        task.PlanRevisions.Add(new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = 2,
            RevisionCause = NyxIdChatPlanRevisionCause.FailureRecovery,
            CommittedAt = completed.UpdatedAt.Clone(),
            AddedStepIds =
            {
                superseded.StepId,
                supersededArtifact.StepId,
                supersededFollowUp.StepId,
            },
        });
        var taskId = task.TaskId;
        var completedBytes = completed.ToByteString();
        var completedSourceBytes = completed.Source.ToByteString();
        var supersededKey = superseded.Operation.Key.Clone();
        var committedBeforeSteering = await eventStore.GetEventsAsync(conversationActorId);
        var steering = CreateSteeringCommand(committedBeforeSteering[^1].Version);
        steering.ConversationActorId = conversationActorId;
        steering.TurnId = originalTurnId;
        steering.SteeringId = "steering-uc2-1";
        steering.ClientRequestId = "client-steering-uc2-1";
        steering.CommandId = "command-steering-uc2-1";
        steering.CorrelationId = "correlation-steering-uc2-1";
        steering.Instruction = "Use 7 pm sharp and require a private room.";
        steering.ToolContext.Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
        {
            OwnerSubject = "owner-alpha",
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = supersededKey.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = "The superseded comparison completed too late.",
                },
            }));
        var continuationTurnId = agent.State.ContinuationAdmission.ContinuationTurnId;
        var selfDispatch = dispatch.Calls.Single(call =>
            call.Envelope.Payload.Is(
                NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor));

        await agent.HandleEventAsync(selfDispatch.Envelope);

        agent.State.ActiveTask.TaskId.Should().Be(taskId);
        agent.State.ActiveTask.PlanRevision.Should().Be(3);
        var preserved = agent.State.ActiveTask.Steps.Single(step =>
            step.StepId == completed.StepId);
        preserved.ToByteString().Should().Equal(completedBytes,
            "a steering revision cannot rewrite completed effect evidence");
        preserved.Source.ToByteString().Should().Equal(completedSourceBytes,
            "provider-authored service and operation provenance must remain verbatim");
        preserved.Status.Should().Be(NyxIdChatStepStatus.Done);
        preserved.Source.Tool.ToolName.Should().Be("web_search");
        preserved.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        preserved.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Succeeded);
        preserved.Substeps.Should().OnlyContain(substep =>
            substep.Status == NyxIdChatSubstepStatus.Done);
        var cancelled = agent.State.ActiveTask.Steps.Single(step =>
            step.StepId == supersededKey.StepId);
        cancelled.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        cancelled.CancelledInPlanRevision.Should().Be(3);
        var cancelledFollowUp = agent.State.ActiveTask.Steps.Single(step =>
            step.StepId == supersededFollowUp.StepId);
        cancelledFollowUp.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        cancelledFollowUp.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Cancelled);
        cancelledFollowUp.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        cancelledFollowUp.CancelledInPlanRevision.Should().Be(3);
        var cancelledArtifact = agent.State.ActiveTask.Steps.Single(step =>
            step.StepId == supersededArtifact.StepId);
        cancelledArtifact.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        cancelledArtifact.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Cancelled);
        cancelledArtifact.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        cancelledArtifact.CancelledInPlanRevision.Should().Be(3);
        agent.State.ActiveTask.PlanRevisions[^1].CancelledStepIds.Should()
            .Equal([
                supersededKey.StepId,
                supersededFollowUp.StepId,
                supersededArtifact.StepId,
            ],
                "transitive cancellation order must be deterministic even when dependents " +
                "appear before their prerequisites in the stored step list");
        agent.State.ActiveTask.PlanRevisions[^1].AddedStepIds.Should().ContainSingle();
        var addedStepId = agent.State.ActiveTask.PlanRevisions[^1].AddedStepIds.Single();
        agent.State.ActiveTask.Steps.Single(step => step.StepId == addedStepId)
            .AddedBy.Should().Be(NyxIdChatStepAddedBy.Steering);

        var revisionStarted = (await eventStore.GetEventsAsync(conversationActorId))
            .Where(item => item.EventData.Is(NyxIdChatTurnStartedEvent.Descriptor))
            .Select(item => item.EventData.Unpack<NyxIdChatTurnStartedEvent>())
            .Single(item => item.State.ActiveTask.PlanRevision == 3);
        var snapshot = NyxIdChatConversationAguiFrameBuilder.BuildStarted(
                conversationActorId,
                continuationTurnId,
                revisionStarted.State)
            .Single(frame => frame.Custom?.Name ==
                             NyxIdChatConversationAguiFrameBuilder.TaskSnapshotEventName)
            .Custom.Payload.Unpack<NyxIdChatTaskState>();
        snapshot.ToByteString().Should().Equal(
            revisionStarted.State.ActiveTask.ToByteString(),
            "every revision must emit the complete committed task snapshot");
        snapshot.Steps.Should().HaveCount(6);
        snapshot.Steps.Single(step => step.StepId == completed.StepId)
            .ToByteString().Should().Equal(completedBytes);
        var continuation = dispatch.OperationCalls.Last().Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        continuation.Key.TurnId.Should().NotBe(originalTurnId);
        continuation.Llm.Request.Prompt.Should()
            .Contain("Steering instruction: Use 7 pm sharp and require a private room.")
            .And.Contain(
                "Original task: Research Greek dinner options for Friday in northern Singapore.")
            .And.Contain(
                "Committed input resolution: input-uc2-logistics; outcome: Accepted")
            .And.Contain(
                $"answer: free text \"{committedCompositeAnswer}\"")
            .And.Contain(
                "Completed step 2: Aevatar web search - find Greek dinner candidates.")
            .And.Contain("tool web_search")
            .And.Contain("Completed substep: Search current web results [status: Done]")
            .And.NotContain("The superseded comparison completed too late.");
        agent.State.ActiveTurn.Prompt.Should().Be(
            "Use 7 pm sharp and require a private room.");
        agent.State.ToString().Should().NotContain(
            "Continue the same committed task",
            "execution context must remain transient");
        classifier.UserMessages.Should().HaveCount(2);
        classifier.UserMessages[^1].Should().Be(continuation.Llm.Request.Prompt,
            "routing and execution must use the same steering context");
        agent.State.LatestInputResolution.Answer.FreeText.Should().Be(
            committedCompositeAnswer);
        var originTerminalDispatch = dispatch.Calls.Single(call =>
            call.Envelope.Payload.Is(NyxIdChatHistoryTerminalDispatchRequested.Descriptor));
        await agent.HandleEventAsync(originTerminalDispatch.Envelope);
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = continuation.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = "The steered research artifact is complete.",
                },
            }));
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded,
            "cancelled old-turn descendants cannot keep the continuation task active");
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        var committedEvents = await eventStore.GetEventsAsync(conversationActorId);
        committedEvents.Should().OnlyContain(item =>
            !item.EventData.ToString().Contains(
                "steering-runtime-token-alpha",
                StringComparison.Ordinal));
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");
    }

    [Theory]
    [InlineData(NyxIdChatToolVerificationDisposition.Applied)]
    [InlineData(NyxIdChatToolVerificationDisposition.NotApplied)]
    public async Task FencedEffectVerification_ShouldResumeVaultedSteeringExactlyOnce(
        NyxIdChatToolVerificationDisposition disposition)
    {
        const string conversationActorId = "conversation-alpha";
        var now = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var vault = new InMemorySecretVault(now);
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, secretVault: vault);
        var operations = new List<string>();
        var dispatch = new RecordingActorDispatchPort(
            operations,
            static (_, _) => Task.CompletedTask);
        var agent = CreateController(
            services,
            conversationActorId,
            actorDispatchPort: dispatch,
            timeProvider: now);
        await agent.ActivateAsync();

        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            agent,
            eventStore,
            dispatch,
            conversationActorId);

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = arranged.Verification.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = arranged.ToolKey.StepId,
                    Disposition = disposition,
                    ReadOperation = arranged.Verification.ToolVerification.ReadBack
                        .ReadOperation.Clone(),
                    CheckName = arranged.Verification.ToolVerification.ReadBack.CheckName,
                },
            }));

        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        var selfDispatch = dispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(
                    NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor))
            .Which;
        selfDispatch.Envelope.ToString().Should().NotContain(
            "steering-runtime-token-alpha");

        await agent.HandleEventAsync(selfDispatch.Envelope);
        await agent.HandleEventAsync(selfDispatch.Envelope.Clone());
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            arranged.Steering.Clone()));

        agent.State.ActiveTurn.TurnId.Should().Be(arranged.ContinuationTurnId);
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        agent.State.PendingSteeringContinuation.Should().BeNull();
        dispatch.OperationCalls.Should().HaveCount(4,
            "the continuation's first LLM operation is dispatched once");
        var continuation = dispatch.OperationCalls[^1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        continuation.Key.TurnId.Should().Be(arranged.ContinuationTurnId);
        continuation.Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(
            "steering-runtime-token-alpha");
        agent.State.ToString().Should().NotContain("steering-runtime-token-alpha");
        (await eventStore.GetEventsAsync(conversationActorId))
            .Select(static evt => evt.EventData.ToString())
            .Should().NotContain(value =>
                value.Contains("steering-runtime-token-alpha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailableFencedVerification_ShouldRemainParkedUntilFixedExpiry()
    {
        const string conversationActorId = "conversation-alpha";
        var now = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var vault = new InMemorySecretVault(now);
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: callbacks,
            secretVault: vault);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(
            services,
            conversationActorId,
            actorDispatchPort: dispatch,
            timeProvider: now);
        await agent.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            agent,
            eventStore,
            dispatch,
            conversationActorId);

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = arranged.Verification.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = arranged.ToolKey.StepId,
                    Disposition = NyxIdChatToolVerificationDisposition.Unavailable,
                    ReadOperation = arranged.Verification.ToolVerification.ReadBack
                        .ReadOperation.Clone(),
                    CheckName = arranged.Verification.ToolVerification.ReadBack.CheckName,
                },
            }));

        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);
        agent.State.PendingSteeringContinuation.Should().NotBeNull();
        dispatch.Calls.Should().NotContain(call => call.Envelope.Payload.Is(
            NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor));
        var expiry = callbacks.TimeoutRequests.Last(request =>
            request.TriggerEnvelope.Payload.Is(
                NyxIdChatPendingSteeringContinuationExpired.Descriptor));
        expiry.DueTime.Should().Be(TimeSpan.FromMinutes(30));

        now.Advance(TimeSpan.FromMinutes(30));
        await agent.HandleEventAsync(expiry.TriggerEnvelope);

        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Rejected);
        agent.State.ContinuationAdmission.ReasonCode.Should().Be(
            "NYXID_CHAT_PENDING_STEERING_CONTINUATION_EXPIRED");
        agent.State.PendingSteeringContinuation.Should().BeNull();
        var finalized = (await eventStore.GetEventsAsync(conversationActorId))[^1]
            .EventData.Unpack<NyxIdChatPendingSteeringContinuationFinalizedEvent>();
        finalized.FailureCode.Should().Be(
            "NYXID_CHAT_PENDING_STEERING_CONTINUATION_EXPIRED");
    }

    [Fact]
    public async Task PendingToolApproval_ShouldExpireAsDenialThroughDurableSelfTimeout()
    {
        const string conversationActorId = "conversation-alpha";
        var now = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: callbacks);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(
            services,
            conversationActorId,
            actorDispatchPort: dispatch,
            timeProvider: now);
        await agent.ActivateAsync();

        agent.State.OwnerSubject = "owner-alpha";
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            WithOwner(CreateStartTurnCommand(), "owner-alpha")));
        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(
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
                            CallId = "call-danger-alpha",
                            ToolName = "repository_delete",
                            ArgumentsJson = "{\"repositoryId\":\"repo-alpha\"}",
                            Safety = new NyxIdChatToolCallSafety
                            {
                                IsDestructive = true,
                                SideEffectKind = "repository.delete",
                            },
                        },
                    },
                },
            }));
        var toolKey = agent.State.ActiveTask.Steps
            .Single(step => step.Kind == NyxIdChatStepKind.Tool)
            .Operation.Key.Clone();

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = toolKey,
                Tool = new NyxIdChatToolOperationResult
                {
                    ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                    Receipt = new Aevatar.AI.Abstractions.AgentToolReceipt
                    {
                        CallId = "call-danger-alpha",
                        ToolName = "repository_delete",
                        Status = Aevatar.AI.Abstractions.AgentToolReceiptStatus.ApprovalRequired,
                        ApprovalRequestId = "approval-alpha",
                        IsDestructive = true,
                    },
                },
            }));

        agent.State.PendingApproval.Should().NotBeNull();
        agent.State.PendingApproval.ExpiresAt.Should().Be(Timestamp.FromDateTimeOffset(
            now.GetUtcNow() + NyxIdChatTaskLifecycle.ToolApprovalExpiryWindow));
        var expiry = callbacks.TimeoutRequests.Last(request =>
            request.TriggerEnvelope.Payload.Is(
                NyxIdChatToolApprovalExpiredSignal.Descriptor));
        expiry.DueTime.Should().Be(NyxIdChatTaskLifecycle.ToolApprovalExpiryWindow);

        now.Advance(NyxIdChatTaskLifecycle.ToolApprovalExpiryWindow);
        await agent.HandleEventAsync(expiry.TriggerEnvelope);

        agent.State.PendingApproval.Should().BeNull();
        agent.State.LatestApprovalResolution.Outcome.Should().Be(
            NyxIdChatNeedsYouResolutionOutcome.Expired);
        agent.State.LatestApprovalResolution.Approved.Should().BeFalse();
        var cancelledStep = agent.State.ActiveTask.Steps
            .Single(step => step.Kind == NyxIdChatStepKind.Tool);
        cancelledStep.Status.Should().Be(NyxIdChatStepStatus.Cancelled);
        cancelledStep.FailureCode.Should().Be(NyxIdChatTaskLifecycle.ApprovalExpired);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Failed);
        agent.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Failed);
        agent.State.ActiveTurn.FailureCode.Should().Be(NyxIdChatTaskLifecycle.ApprovalExpired);
        dispatch.OperationCalls.Should().NotContain(call =>
            call.Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>().InputCase ==
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation);

        var versionAfterExpiry = (await eventStore.GetEventsAsync(conversationActorId))[^1].Version;
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatApprovalResolveCommand
            {
                ScopeId = agent.State.ScopeId,
                ConversationActorId = conversationActorId,
                RequestId = "approval-alpha",
                ClientRequestId = "client-late-approve",
                Approved = true,
                ExpectedStateVersion = versionAfterExpiry,
            }));

        (await eventStore.GetEventsAsync(conversationActorId))[^1].Version
            .Should().Be(versionAfterExpiry,
                "a late approve against the committed expiry cannot advance actor state");
        dispatch.OperationCalls.Should().NotContain(call =>
            call.Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>().InputCase ==
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation);
    }

    [Fact]
    public async Task WrongPendingSteeringSignal_ShouldNotDestroyCurrentContinuation()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            agent,
            eventStore,
            dispatch,
            conversationActorId);
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = arranged.Verification.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = arranged.ToolKey.StepId,
                    Disposition = NyxIdChatToolVerificationDisposition.Applied,
                    ReadOperation = arranged.Verification.ToolVerification.ReadBack
                        .ReadOperation.Clone(),
                    CheckName = arranged.Verification.ToolVerification.ReadBack.CheckName,
                },
            }));
        var before = await eventStore.GetEventsAsync(conversationActorId);
        var pending = agent.State.PendingSteeringContinuation.Clone();
        var wrong = dispatch.Calls.Single(call => call.Envelope.Payload.Is(
            NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor)).Envelope.Clone();
        wrong.Payload = Any.Pack(new NyxIdChatPendingSteeringContinuationDispatchRequested
        {
            TurnId = arranged.ContinuationTurnId,
            CredentialRef = "wrong-ref",
        });

        await agent.HandleEventAsync(wrong);

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(before.Count);
        agent.State.PendingSteeringContinuation.Should().BeEquivalentTo(pending);
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        dispatch.OperationCalls.Should().HaveCount(3);
    }

    [Fact]
    public async Task Activation_ShouldRecoverAcceptedPendingSteeringFromVault()
    {
        const string conversationActorId = "conversation-alpha";
        var now = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var vault = new InMemorySecretVault(now);
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: callbacks,
            secretVault: vault);
        var initialDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var initial = CreateController(
            services,
            conversationActorId,
            initialDispatch,
            timeProvider: now);
        await initial.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            initial,
            eventStore,
            initialDispatch,
            conversationActorId);
        await AcceptPendingSteeringAfterVerificationAsync(
            initial,
            arranged,
            conversationActorId);

        callbacks.TimeoutRequests.Clear();
        var recoveredDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var recovered = CreateController(
            services,
            conversationActorId,
            recoveredDispatch,
            timeProvider: now);
        await recovered.ActivateAsync();
        var recovery = callbacks.TimeoutRequests.Single(request =>
            request.TriggerEnvelope.Payload.Is(
                NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor));

        await recovered.HandleEventAsync(recovery.TriggerEnvelope);

        recovered.State.ActiveTurn.TurnId.Should().Be(arranged.ContinuationTurnId);
        recovered.State.PendingSteeringContinuation.Should().BeNull();
        recoveredDispatch.OperationCalls.Should().ContainSingle();
        recoveredDispatch.OperationCalls.Single().Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>()
            .Llm.Request.LlmControl.NyxIdAccessToken.Should().Be(
                "steering-runtime-token-alpha");
    }

    [Fact]
    public async Task ActivationAfterStartCommit_ShouldOnlyCleanupRetainedVaultRef()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore);
        var initialDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var initial = CreateController(services, conversationActorId, initialDispatch);
        await initial.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            initial,
            eventStore,
            initialDispatch,
            conversationActorId);
        await AcceptPendingSteeringAfterVerificationAsync(
            initial,
            arranged,
            conversationActorId);
        var startedState = initial.State.Clone();
        startedState.ContinuationAdmission.Status =
            NyxIdChatContinuationAdmissionStatus.Started;
        startedState.ActiveTurn.TurnId = arranged.ContinuationTurnId;
        startedState.ActiveTurn.Status = NyxIdChatTurnStatus.Active;
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        await PersistTestStateAsync(
            eventStore,
            conversationActorId,
            committed[^1].Version + 1,
            startedState);

        var recoveredDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var recovered = CreateController(services, conversationActorId, recoveredDispatch);
        await recovered.ActivateAsync();

        recovered.State.PendingSteeringContinuation.Should().BeNull();
        recovered.State.ActiveTurn.TurnId.Should().Be(arranged.ContinuationTurnId);
        recoveredDispatch.OperationCalls.Should().BeEmpty(
            "activation must not restart a continuation whose start event is already committed");
        (await eventStore.GetEventsAsync(conversationActorId))[^1].EventData.Is(
            NyxIdChatPendingSteeringContinuationFinalizedEvent.Descriptor).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, NyxIdChatPendingSteeringContinuationOutcome.SecretUnavailable)]
    [InlineData(true, NyxIdChatPendingSteeringContinuationOutcome.IdentityMismatch)]
    public async Task MissingOrMismatchedVaultCommand_ShouldRejectAndCleanup(
        bool replaceCommand,
        NyxIdChatPendingSteeringContinuationOutcome expectedOutcome)
    {
        const string conversationActorId = "conversation-alpha";
        var now = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var vault = new InMemorySecretVault(now);
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, secretVault: vault);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(
            services,
            conversationActorId,
            dispatch,
            timeProvider: now);
        await agent.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            agent,
            eventStore,
            dispatch,
            conversationActorId);
        var signal = await AcceptPendingSteeringAfterVerificationAsync(
            agent,
            arranged,
            conversationActorId);
        var pending = agent.State.PendingSteeringContinuation.Clone();
        if (replaceCommand)
        {
            var mismatched = WithOwner(CreateStartTurnCommand(), "owner-alpha");
            await vault.RotateAsync(new RotateSecretRequest(
                pending.Ref,
                pending.Purpose,
                pending.OwnerScopeKey,
                pending.SubjectId,
                Convert.ToBase64String(mismatched.ToByteArray()),
                "test mismatched pending steering command"));
        }
        else
        {
            await vault.RevokeAsync(new RevokeSecretRequest(
                pending.Ref,
                pending.Purpose,
                pending.OwnerScopeKey,
                pending.SubjectId,
                "test missing pending steering command"));
        }

        await agent.HandleEventAsync(signal);

        agent.State.PendingSteeringContinuation.Should().BeNull();
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Rejected);
        var finalized = (await eventStore.GetEventsAsync(conversationActorId))[^1]
            .EventData.Unpack<NyxIdChatPendingSteeringContinuationFinalizedEvent>();
        finalized.Outcome.Should().Be(expectedOutcome);
        dispatch.OperationCalls.Should().HaveCount(3);
    }

    [Fact]
    public async Task TransientVaultFailure_ShouldAutomaticallyRetryAndStartExactlyOnce()
    {
        const string conversationActorId = "conversation-alpha";
        var now = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var vault = new TransientResolveSecretVault(new InMemorySecretVault(now));
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: callbacks,
            secretVault: vault);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(
            services,
            conversationActorId,
            dispatch,
            timeProvider: now);
        await agent.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            agent,
            eventStore,
            dispatch,
            conversationActorId);
        var signal = await AcceptPendingSteeringAfterVerificationAsync(
            agent,
            arranged,
            conversationActorId);
        vault.FailResolution = true;
        now.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(58));
        var before = await eventStore.GetEventsAsync(conversationActorId);

        await agent.HandleEventAsync(signal);

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(before.Count);
        agent.State.PendingSteeringContinuation.Should().NotBeNull();
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        var retry = callbacks.TimeoutRequests.Last(request =>
            request.TriggerEnvelope.Payload.Is(
                NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor));
        retry.DueTime.Should().Be(TimeSpan.FromMilliseconds(1999),
            "retry cadence must not extend the committed absolute expiry");
        vault.FailResolution = false;
        now.Advance(retry.DueTime);

        await agent.HandleEventAsync(retry.TriggerEnvelope);
        await agent.HandleEventAsync(retry.TriggerEnvelope.Clone());
        await agent.HandleEventAsync(signal.Clone());

        agent.State.PendingSteeringContinuation.Should().BeNull();
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        dispatch.OperationCalls.Should().HaveCount(4);
    }

    [Fact]
    public async Task MissingVault_ShouldAutomaticallyRetryAfterVaultIsRestored()
    {
        const string conversationActorId = "conversation-alpha";
        var now = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var eventStore = new InMemoryEventStoreForTests();
        var initialCallbacks = new RecordingRuntimeCallbackScheduler();
        using var initialServices = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: initialCallbacks,
            secretVault: new InMemorySecretVault(now));
        var initialDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var initial = CreateController(
            initialServices,
            conversationActorId,
            initialDispatch,
            timeProvider: now);
        await initial.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            initial,
            eventStore,
            initialDispatch,
            conversationActorId);
        await AcceptPendingSteeringAfterVerificationAsync(
            initial,
            arranged,
            conversationActorId);

        var recoveryCallbacks = new RecordingRuntimeCallbackScheduler();
        using var recoveryServices = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: recoveryCallbacks,
            registerSecretVault: false);
        var recoveryDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var recovered = CreateController(
            recoveryServices,
            conversationActorId,
            recoveryDispatch,
            timeProvider: now);
        await recovered.ActivateAsync();
        var resume = recoveryCallbacks.TimeoutRequests.Single(request =>
            request.TriggerEnvelope.Payload.Is(
                NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor));
        var before = await eventStore.GetEventsAsync(conversationActorId);

        await recovered.HandleEventAsync(resume.TriggerEnvelope);

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(before.Count);
        recovered.State.PendingSteeringContinuation.Should().NotBeNull();
        var retry = recoveryCallbacks.TimeoutRequests.Last(request =>
            request.TriggerEnvelope.Payload.Is(
                NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor));
        retry.DueTime.Should().Be(TimeSpan.FromSeconds(5));
        recovered.Services = initialServices;

        await recovered.HandleEventAsync(retry.TriggerEnvelope);
        await recovered.HandleEventAsync(retry.TriggerEnvelope.Clone());

        recovered.State.PendingSteeringContinuation.Should().BeNull();
        recovered.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        recoveryDispatch.OperationCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task StartCommitFailure_ShouldAutomaticallyRetryAndStartExactlyOnce()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new FailTurnStartEventStore(new InMemoryEventStoreForTests());
        var callbacks = new RecordingRuntimeCallbackScheduler();
        using var services = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: callbacks);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            agent,
            eventStore,
            dispatch,
            conversationActorId);
        var signal = await AcceptPendingSteeringAfterVerificationAsync(
            agent,
            arranged,
            conversationActorId);
        var before = await eventStore.GetEventsAsync(conversationActorId);
        eventStore.FailTurnStart = true;

        await agent.HandleEventAsync(signal);

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(before.Count);
        agent.State.PendingSteeringContinuation.Should().NotBeNull();
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        var retry = callbacks.TimeoutRequests.Last(request =>
            request.TriggerEnvelope.Payload.Is(
                NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor));
        retry.DueTime.Should().Be(TimeSpan.FromSeconds(5));
        eventStore.FailTurnStart = false;

        await agent.HandleEventAsync(retry.TriggerEnvelope);
        await agent.HandleEventAsync(retry.TriggerEnvelope.Clone());
        await agent.HandleEventAsync(signal.Clone());

        agent.State.PendingSteeringContinuation.Should().BeNull();
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        dispatch.OperationCalls.Should().HaveCount(4);
    }

    [Fact]
    public async Task PostCommitContinuationDispatchFailure_ShouldFinalizeAdmissionBehindDeliveryProbe()
    {
        const string conversationActorId = "conversation-alpha";
        var callbacks = new RecordingRuntimeCallbackScheduler();
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: callbacks);
        var failContinuationDispatch = false;
        var dispatch = new RecordingActorDispatchPort(
            [],
            (_, envelope) =>
            {
                if (failContinuationDispatch &&
                    envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor))
                {
                    throw new InvalidOperationException("continuation operation dispatch unavailable");
                }

                return Task.CompletedTask;
            });
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            agent,
            eventStore,
            dispatch,
            conversationActorId);
        var signal = await AcceptPendingSteeringAfterVerificationAsync(
            agent,
            arranged,
            conversationActorId);
        failContinuationDispatch = true;

        await agent.HandleEventAsync(signal);

        agent.State.ActiveTurn.TurnId.Should().Be(arranged.ContinuationTurnId);
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        agent.State.PendingSteeringContinuation.Should().BeNull();
        var uncertainStep = agent.State.ActiveTask.Steps.Single(step =>
            step.Operation?.Key.TurnId == arranged.ContinuationTurnId);
        uncertainStep.Status.Should().Be(NyxIdChatStepStatus.Running);
        uncertainStep.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Dispatched);
        agent.State.PendingOperationDeliveryProbe.Should().BeEquivalentTo(
            uncertainStep.Operation.Key);
        AssertSingleOperationDeliveryProbe(dispatch, uncertainStep.Operation.Key);
        dispatch.RecoveryCalls.Should().BeEmpty();
        (await eventStore.GetEventsAsync(conversationActorId))[^1].EventData.Is(
            NyxIdChatPendingSteeringContinuationFinalizedEvent.Descriptor).Should().BeTrue();

        failContinuationDispatch = false;
        callbacks.TimeoutRequests.Clear();
        var recoveredDispatch = new RecordingActorDispatchPort(
            [],
            static (_, _) => Task.CompletedTask);
        var recovered = CreateController(
            services,
            conversationActorId,
            recoveredDispatch);
        await recovered.ActivateAsync();
        var probeRetry = callbacks.TimeoutRequests.Should().ContainSingle(request =>
                request.TriggerEnvelope.Payload.Is(
                    NyxIdChatOperationDeliveryProbeDispatchRequested.Descriptor))
            .Which;
        callbacks.TimeoutRequests.Should().NotContain(request =>
            request.TriggerEnvelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor));

        await recovered.HandleEventAsync(probeRetry.TriggerEnvelope);

        recoveredDispatch.OperationCalls.Should().BeEmpty(
            "activation cannot replay model I/O while delivery admission is unknown");
        var stillUncertain = recovered.State.ActiveTask.Steps.Single(step =>
            step.Operation?.Key.TurnId == arranged.ContinuationTurnId);
        stillUncertain.Status.Should().Be(NyxIdChatStepStatus.Running);
        stillUncertain.Operation.Phase.Should().Be(NyxIdChatOperationPhase.Dispatched);
        recovered.State.PendingOperationDeliveryProbe.Should().BeEquivalentTo(
            stillUncertain.Operation.Key);
        AssertSingleOperationDeliveryProbe(recoveredDispatch, stillUncertain.Operation.Key);
        stillUncertain.AvailableActions.Retry.Should().BeFalse(
            "retry cannot unlock before the turn reports admitted or fences late delivery");
        recovered.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        recovered.State.PendingSteeringContinuation.Should().BeNull();
        (await eventStore.GetEventsAsync(conversationActorId))
            .Where(item => item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            .Select(item => item.EventData.Unpack<NyxIdChatOperationReconciledEvent>())
            .Count(item => item.Result?.Key?.TurnId == arranged.ContinuationTurnId)
            .Should().Be(0,
                "conversation recovery stays fenced until the turn answers the delivery probe");
    }

    [Fact]
    public async Task ContinuationRetrySchedulerFailure_ShouldRecoverFromCommittedPendingStateAfterRestart()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new FailTurnStartEventStore(new InMemoryEventStoreForTests());
        var callbacks = new RecordingRuntimeCallbackScheduler();
        using var services = BuildEventSourcingServices(
            eventStore,
            callbackScheduler: callbacks);
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var agent = CreateController(services, conversationActorId, dispatch);
        await agent.ActivateAsync();
        var arranged = await ArrangePendingEffectSteeringVerificationAsync(
            agent,
            eventStore,
            dispatch,
            conversationActorId);
        var signal = await AcceptPendingSteeringAfterVerificationAsync(
            agent,
            arranged,
            conversationActorId);
        eventStore.FailTurnStart = true;
        callbacks.ScheduleException = new InvalidOperationException("scheduler unavailable");

        await FluentActions.Invoking(() => agent.HandleEventAsync(signal))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("scheduler unavailable");
        agent.State.PendingSteeringContinuation.Should().NotBeNull();
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);

        eventStore.FailTurnStart = false;
        callbacks.ScheduleException = null;
        callbacks.TimeoutRequests.Clear();
        var recoveredDispatch = new RecordingActorDispatchPort(
            [],
            static (_, _) => Task.CompletedTask);
        var recovered = CreateController(
            services,
            conversationActorId,
            recoveredDispatch);
        await recovered.ActivateAsync();
        var retry = callbacks.TimeoutRequests.Should().ContainSingle(request =>
                request.TriggerEnvelope.Payload.Is(
                    NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor))
            .Which;

        await recovered.HandleEventAsync(retry.TriggerEnvelope);
        await recovered.HandleEventAsync(retry.TriggerEnvelope.Clone());

        recovered.State.ActiveTurn.TurnId.Should().Be(arranged.ContinuationTurnId);
        recovered.State.PendingSteeringContinuation.Should().BeNull();
        recovered.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Started);
        recoveredDispatch.OperationCalls.Should().ContainSingle(
            "the restarted actor must start the delayed continuation exactly once");
    }

    [Fact]
    public async Task DelayedSteeringWithoutOwner_ShouldFailBeforeVaultWrite()
    {
        const string conversationActorId = "conversation-alpha";
        var vault = new CountingSecretVault(new InMemorySecretVault());
        var eventStore = new InMemoryEventStoreForTests();
        using var services = BuildEventSourcingServices(eventStore, secretVault: vault);
        var agent = CreateController(services, conversationActorId);
        await agent.ActivateAsync();
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            CreateStartTurnCommand()));
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var steering = CreateSteeringCommand(committed[^1].Version);

        var act = () => agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*owner_subject*");
        vault.PutCount.Should().Be(0);
        agent.State.PendingSteeringContinuation.Should().BeNull();
        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(committed.Count);
    }

    [Fact]
    public async Task SteeringAcceptedCheckpointReplay_ShouldRedispatchSameSelfContinuationAfterDeliveryGap()
    {
        const string conversationActorId = "conversation-alpha";
        var eventStore = new InMemoryEventStoreForTests();
        var callbacks = new RecordingRuntimeCallbackScheduler();
        using var services = BuildEventSourcingServices(eventStore, callbackScheduler: callbacks);
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
        callbacks.TimeoutRequests.Clear();
        await reactivated.ActivateAsync();
        var steering = CreateSteeringCommand(expectedStateVersion: checkpointVersion);
        var activationRecovery = callbacks.TimeoutRequests.Should().ContainSingle(request =>
                request.TriggerEnvelope.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor))
            .Which
            .TriggerEnvelope.Clone();
        activationRecovery.Payload.Is(NyxIdChatRecoveryRequestedSignal.Descriptor).Should().BeTrue(
            "activation queues typed recovery for the requested LLM waterline");
        dispatch.RecoveryCalls.Should().BeEmpty();

        await reactivated.HandleEventAsync(CreateEnvelope(conversationActorId, steering));

        reactivated.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.Accepted);
        dispatch.StartTurnCalls.Should().ContainSingle(
            "steering queues one continuation");
        var firstSelfMessage = dispatch.Calls.Single(call =>
            call.Envelope.Payload.Is(NyxIdChatStartTurnCommand.Descriptor)).Envelope.Clone();
        var beforeReplay = await eventStore.GetEventsAsync(conversationActorId);

        await reactivated.HandleEventAsync(activationRecovery);

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(
            beforeReplay.Count,
            "the steering commits advance the version and make the earlier activation recovery stale");
        dispatch.RecoveryCalls.Should().BeEmpty(
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
        agent.State.OwnerSubject = "owner-alpha";
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            WithOwner(CreateStartTurnCommand(), "owner-alpha")));
        var afterStart = await eventStore.GetEventsAsync(conversationActorId);
        var steering = CreateSteeringCommand(afterStart[^1].Version);
        steering.ToolContext.Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
        {
            OwnerSubject = "owner-alpha",
        };
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
    public async Task Uc2CompositeInput_ShouldResolveResearchScopeOnceAndResumeAfterReload()
    {
        const string conversationActorId = "conversation-uc2";
        const string compositeQuestion =
            "Please answer together: party size, dietary restrictions, budget cap, and whether you accept a research-only shortlist because no reservation can be placed.";
        const string rawAnswer =
            "Party of 6, one vegetarian, no budget cap. Yes - research and prepare a ready-to-book shortlist.";
        const string runtimeToken = "uc2-composite-runtime-token";
        var eventStore = new InMemoryEventStoreForTests();
        var dispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        using var services = BuildEventSourcingServices(eventStore);
        var initial = CreateController(services, conversationActorId, dispatch);
        await initial.ActivateAsync();
        var start = CreateStartTurnCommand();
        start.ConversationActorId = conversationActorId;
        start.TurnId = "turn-uc2-1";
        start.TaskId = "task-uc2";
        start.ClientRequestId = "client-uc2-1";
        start.CommandId = "command-uc2-1";
        start.CorrelationId = "correlation-uc2-1";
        start.Prompt =
            "Book a dinner reservation for the team on Friday - Greek food, northern Singapore, 6-7 pm.";
        await initial.HandleEventAsync(CreateEnvelope(conversationActorId, start));
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
                            CallId = "call-ask-user-composite",
                            ToolName = "ask_user",
                            ArgumentsJson = $$"""
                                {
                                  "question": "{{compositeQuestion}}",
                                  "allow_free_text": true
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
        await initial.HandleEventAsync(selfRequest);
        initial.State.PendingInput.Should().NotBeNull();
        var pending = initial.State.PendingInput!;
        pending.Prompt.Should().Be(compositeQuestion);
        pending.TurnId.Should().Be("turn-uc2-1");
        pending.TaskId.Should().Be("task-uc2");
        pending.Options.Should().BeEmpty();
        pending.AllowFreeText.Should().BeTrue();
        pending.MultiSelect.Should().BeFalse();
        initial.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.Input &&
            step.Status == NyxIdChatStepStatus.Waiting);

        var recoveryDispatch = new RecordingActorDispatchPort([], static (_, _) => Task.CompletedTask);
        var recovered = CreateController(services, conversationActorId, recoveryDispatch);
        await recovered.ActivateAsync();
        var committedBeforeResolution = await eventStore.GetEventsAsync(conversationActorId);
        await recovered.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatInputResolveCommand
            {
                ScopeId = "scope-alpha",
                ConversationActorId = conversationActorId,
                RequestId = pending.RequestId,
                ClientRequestId = "client-input-composite",
                Answer = new NyxIdChatInputAnswer { FreeText = rawAnswer },
                ExpectedStateVersion = committedBeforeResolution.Count,
                CommandId = "command-input-composite",
                CorrelationId = "correlation-input-composite",
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Credentials = new AgentToolCredentialsPayload
                    {
                        NyxIdAccessToken = runtimeToken,
                    },
                },
            }));

        recovered.State.PendingInput.Should().BeNull();
        recovered.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.Input &&
            step.Status == NyxIdChatStepStatus.Done);
        recovered.State.ActiveTask.Steps.Should().ContainSingle(step =>
            step.Kind == NyxIdChatStepKind.Llm &&
            step.Status == NyxIdChatStepStatus.Running &&
            step.AddedBy == NyxIdChatStepAddedBy.Replan);
        recovered.State.ActiveTask.TaskId.Should().Be("task-uc2");
        recovered.State.ActiveTask.PlanRevision.Should().Be(3);
        var continuation = recoveryDispatch.OperationCalls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        continuation.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation);
        continuation.InputContinuation.Answer.FreeText.Should().Be(rawAnswer);
        continuation.InputContinuation.ToolCallId.Should().Be("call-ask-user-composite");
        continuation.InputContinuation.ToolContext.Credentials.NyxIdAccessToken.Should()
            .Be(runtimeToken);

        const string searchArguments =
            "{\"query\":\"Greek dinner northern Singapore Friday 6 to 7 pm\",\"max_results\":5}";
        const string communicatedPlan =
            "Plan: use Aevatar web_search, then draft a verified research-only shortlist. " +
            "Execution is automatic for read and draft operations. No reservation will be made.";
        await recovered.HandleOperationProgressAsync(new NyxIdChatOperationProgressSignal
        {
            Key = continuation.Key.Clone(),
            Sequence = 1,
            Text = new NyxIdChatTextProgress { Delta = communicatedPlan },
        });
        var planProgress = (await eventStore.GetEventsAsync(conversationActorId))[^1]
            .EventData.Unpack<NyxIdChatOperationProgressedEvent>();
        NyxIdChatConversationAguiFrameBuilder.BuildProgressed("turn-uc2-1", planProgress)
            .Should().ContainSingle(frame =>
                frame.TextMessageContent != null &&
                frame.TextMessageContent.Delta == communicatedPlan);
        await recovered.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = continuation.Key.Clone(),
                Llm = new NyxIdChatLLMOperationResult
                {
                    Content = communicatedPlan,
                    ToolCalls =
                    {
                        new NyxIdChatToolCall
                        {
                            CallId = "call-uc2-search",
                            ToolName = "web_search",
                            ArgumentsJson = searchArguments,
                            Safety = new NyxIdChatToolCallSafety
                            {
                                IsReadOnly = true,
                                MayChangeExternalState = false,
                                SideEffectKind = "web.search",
                            },
                        },
                    },
                },
            }));

        var search = recovered.State.ActiveTask.Steps.Single(step =>
            step.Source?.SourceCase == NyxIdChatStepSource.SourceOneofCase.Tool &&
            step.Source.Tool.ToolName == "web_search");
        search.MayChangeExternalState.Should().BeFalse();
        search.Status.Should().Be(NyxIdChatStepStatus.Running);
        var searchDispatch = recoveryDispatch.OperationCalls.Last().Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        searchDispatch.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool);
        searchDispatch.Tool.ToolName.Should().Be("web_search");
        searchDispatch.Tool.ArgumentsJson.Should().Be(searchArguments);
        recoveryDispatch.OperationCalls.Should().HaveCount(2,
            "one input continuation and one exact read-only search must run after consent");

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var indexedEvents = committed.Select(static (item, index) => (item, index)).ToArray();
        indexedEvents.Single(entry =>
                entry.item.EventData.Is(NyxIdChatOperationProgressedEvent.Descriptor)).index
            .Should().BeLessThan(indexedEvents.Last(entry =>
                    entry.item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor)).index,
                "the communicated plan must be observable before the tool plan dispatches");
        var inputResolution = committed.Should().ContainSingle(item =>
                item.EventData.Is(NyxIdChatInputResolutionCommittedEvent.Descriptor))
            .Which.EventData.Unpack<NyxIdChatInputResolutionCommittedEvent>();
        inputResolution.Resolution.Answer.FreeText.Should().Be(rawAnswer);
        committed.Should().OnlyContain(item =>
            !item.EventData.ToString().Contains(runtimeToken, StringComparison.Ordinal));
        recovered.State.LatestInputResolution.Answer.FreeText.Should().Be(rawAnswer);
        recovered.State.ToString().Should().NotContain(runtimeToken);
    }

    [Fact]
    public async Task InputResolution_WhenContinuationDispatchFails_ShouldWaitForDeliveryProbe()
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
        controller.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        controller.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Active);
        var uncertainContinuation = controller.State.ActiveTask.Steps.Should()
            .ContainSingle(step =>
                step.Kind == NyxIdChatStepKind.Llm &&
                step.AddedBy == NyxIdChatStepAddedBy.Replan)
            .Which;
        uncertainContinuation.Status.Should().Be(NyxIdChatStepStatus.Running);
        uncertainContinuation.Operation.Phase.Should().Be(
            NyxIdChatOperationPhase.Dispatched);
        controller.State.PendingOperationDeliveryProbe.Should().BeEquivalentTo(
            uncertainContinuation.Operation.Key);
        AssertSingleOperationDeliveryProbe(dispatch, uncertainContinuation.Operation.Key);
        dispatch.RecoveryCalls.Should().BeEmpty(
            "the conversation cannot recover or advance before the turn fences late delivery");
        dispatch.OperationCalls.Count(call =>
                call.Envelope.Payload.Unpack<NyxIdChatOperationDispatchCommand>().InputCase ==
                NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation)
            .Should().Be(1,
                "the accepted answer must dispatch one continuation without speculative replay");
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().Contain(item =>
            item.EventData.Is(NyxIdChatInputResolutionCommittedEvent.Descriptor));
        committed.Should().ContainSingle(item =>
            item.EventData.Is(NyxIdChatOperationDispatchUncertainEvent.Descriptor) &&
            item.EventData.Unpack<NyxIdChatOperationDispatchUncertainEvent>()
                .Key.Equals(uncertainContinuation.Operation.Key));
        committed.Should().NotContain(item =>
            item.EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor) &&
            item.EventData.Unpack<NyxIdChatOperationReconciledEvent>().Result != null &&
            item.EventData.Unpack<NyxIdChatOperationReconciledEvent>().Result.Failure != null &&
            item.EventData.Unpack<NyxIdChatOperationReconciledEvent>()
                .Result.Failure.FailureCode == "NYXID_CHAT_OPERATION_DISPATCH_FAILED");
        committed.Should().OnlyContain(item =>
            !item.EventData.ToString().Contains(refreshedToken, StringComparison.Ordinal));
    }

    private static NyxIdChatTurnOperationDeliveryProbeCommand AssertSingleOperationDeliveryProbe(
        RecordingActorDispatchPort dispatch,
        NyxIdChatOperationKey expectedKey)
    {
        var probe = dispatch.Calls.Should().ContainSingle(call =>
                call.Envelope.Payload.Is(NyxIdChatTurnOperationDeliveryProbeCommand.Descriptor))
            .Which.Envelope.Payload.Unpack<NyxIdChatTurnOperationDeliveryProbeCommand>();
        probe.Key.Should().BeEquivalentTo(expectedKey);
        return probe;
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

    private static NyxIdChatTaskStepState CreateCompletedReadOnlySearchStep(
        string conversationActorId,
        string turnId,
        string taskId)
    {
        var completedAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 24, 7, 59, 0, TimeSpan.Zero));
        var step = new NyxIdChatTaskStepState
        {
            StepId = "step-uc2-search",
            Order = 1,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Done,
            Required = true,
            Description = "Aevatar web search - find Greek dinner candidates.",
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = "web_search",
                },
            },
            MayChangeExternalState = false,
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = conversationActorId,
                    TurnId = turnId,
                    TaskId = taskId,
                    StepId = "step-uc2-search",
                    OperationId = "operation-uc2-search",
                    OperationGeneration = 1,
                },
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Succeeded,
                MayChangeExternalState = false,
                Idempotent = true,
                IdempotencyKey = "operation-uc2-search",
                RequestedAt = completedAt.Clone(),
                DispatchedAt = completedAt.Clone(),
                CompletedAt = completedAt.Clone(),
                TerminalCode = "NYXID_TOOL_SUCCEEDED",
                LatestProgressSequence = 7,
                LastProgressAt = completedAt.Clone(),
            },
            AddedBy = NyxIdChatStepAddedBy.Initial,
            AddedInPlanRevision = 1,
            UpdatedAt = completedAt.Clone(),
        };
        step.Substeps.Add(new NyxIdChatSubstepState
        {
            SubstepId = "prepare-operation",
            Title = "Build search query",
            Status = NyxIdChatSubstepStatus.Done,
        });
        step.Substeps.Add(new NyxIdChatSubstepState
        {
            SubstepId = "execute-operation",
            Title = "Search current web results",
            Status = NyxIdChatSubstepStatus.Done,
        });
        return step;
    }

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
                PlanId = "plan-alpha",
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
        var blocked = NyxIdChatBrowserActions.RequestAuthorization(
            state,
            signal,
            CreateActionRegistry(),
            Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero))).State;
        return blocked;
    }

    private static NyxIdChatConversationGAgentState CreateActorOwnedPostconditionState(
        string conversationActorId,
        NyxIdChatOperationKey key)
    {
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = conversationActorId,
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = key.TurnId,
                TaskId = key.TaskId,
                Status = NyxIdChatTurnStatus.Active,
                Intent = NyxIdChatTurnIntent.ServiceConnect,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = key.TurnId,
                TaskId = key.TaskId,
                PlanId = "plan-service-connect",
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = key.StepId,
                ActiveOperationId = key.OperationId,
            },
            HistoryDeliveryReservation = new NyxIdChatHistoryDeliveryReservationState
            {
                DeliveryId = "delivery-service-connect",
                ScopeId = "scope-alpha",
                ConversationId = conversationActorId,
                TurnId = key.TurnId,
                SourceActorId = conversationActorId,
                SourceCommandId = "command-service-connect",
                SourceCorrelationId = "correlation-service-connect",
                Dispatched = true,
                Attempt = 1,
            },
        };
        state.LatestTurn = state.ActiveTurn.Clone();
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    ActionRequestId = "actor-owned-service-connected",
                    Check = "service.connected",
                    ProviderResourceId = "user-service-alpha",
                },
            },
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Running,
            },
        });
        return state;
    }

    private static NyxIdChatConversationGAgentState CreateVerifiedAuthorizationContinuationState(
        string conversationActorId)
    {
        const string originTurnId = "turn-origin-alpha";
        const string continuationTurnId = "turn-continuation-beta";
        const string taskId = "task-alpha";
        const string actionRequestId = "action-alpha";
        const string postconditionStepId = "step-postcondition-alpha";
        var continuationKey = new NyxIdChatOperationKey
        {
            ConversationActorId = conversationActorId,
            TurnId = originTurnId,
            TaskId = taskId,
            StepId = "step-continuation-alpha",
            OperationId = "operation-continuation-alpha",
            OperationGeneration = 1,
        };
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = conversationActorId,
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = continuationTurnId,
                TaskId = taskId,
                Status = NyxIdChatTurnStatus.Active,
                Intent = NyxIdChatTurnIntent.Unspecified,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = continuationTurnId,
                TaskId = taskId,
                PlanId = "plan-alpha",
                PlanRevision = 2,
                PlanRevisionHistoryStart = 1,
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = continuationKey.StepId,
                ActiveOperationId = continuationKey.OperationId,
            },
            ContinuationAdmission = new NyxIdChatContinuationAdmissionState
            {
                Kind = NyxIdChatContinuationKind.Action,
                RequestId = "command-continuation-alpha",
                OriginTurnId = originTurnId,
                ContinuationTurnId = continuationTurnId,
                Status = NyxIdChatContinuationAdmissionStatus.Accepted,
                OwnerSubject = "owner-alpha",
                ActionReports =
                {
                    new NyxIdChatActionReport
                    {
                        ActionRequestId = actionRequestId,
                        OriginTurnId = originTurnId,
                        Disposition = NyxIdChatActionDisposition.Completed,
                        Resource = new NyxIdChatSafeResourceRef
                        {
                            UserService = new NyxIdChatUserServiceRef
                            {
                                UserServiceId = "us-alpha",
                            },
                        },
                    },
                },
            },
            HistoryDeliveryReservation = new NyxIdChatHistoryDeliveryReservationState
            {
                DeliveryId = "delivery-continuation-alpha",
                ScopeId = "scope-alpha",
                ConversationId = conversationActorId,
                TurnId = continuationTurnId,
                UserText = "NyxID action update: completed.",
                SourceActorId = conversationActorId,
                SourceCommandId = "command-continuation-alpha",
                SourceCorrelationId = "correlation-continuation-alpha",
                RequestFingerprint = "fingerprint-continuation-alpha",
                CreateConversationIfMissing = true,
                ExposeCreateRecovery = false,
                Dispatched = true,
                Attempt = 1,
            },
            ProgressSequence = 10,
            UpdatedAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero)),
        };
        state.LatestTurn = state.ActiveTurn.Clone();
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-source-tool-alpha",
            Order = 1,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Done,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = "nyxid_require_service",
                    AuthorizationReadiness = new NyxIdChatAuthorizationReadinessInput
                    {
                        ToolName = "nyxid_require_service",
                        Params = new NyxIdChatRequireServiceParams
                        {
                            ServiceSlug = "service-alpha",
                            RequestedScopes = { "items:read" },
                        },
                    },
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = conversationActorId,
                    TurnId = originTurnId,
                    TaskId = taskId,
                    StepId = "step-source-tool-alpha",
                    OperationId = "operation-source-tool-alpha",
                    OperationGeneration = 1,
                },
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Succeeded,
            },
        });
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = postconditionStepId,
            Order = 2,
            Kind = NyxIdChatStepKind.Postcondition,
            Status = NyxIdChatStepStatus.Done,
            Required = true,
            ActionRequestId = actionRequestId,
            Source = new NyxIdChatStepSource
            {
                Postcondition = new NyxIdChatPostconditionStepSource
                {
                    ActionRequestId = actionRequestId,
                    Check = "ServiceAccessReview",
                },
            },
            DependsOn = { "step-source-tool-alpha" },
            ExternalEffect = NyxIdChatEffectEvidence.Confirmed,
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = conversationActorId,
                    TurnId = originTurnId,
                    TaskId = taskId,
                    StepId = postconditionStepId,
                    OperationId = "operation-postcondition-alpha",
                    OperationGeneration = 1,
                },
                Kind = NyxIdChatStepKind.Postcondition,
                Phase = NyxIdChatOperationPhase.Succeeded,
            },
        });
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = continuationKey.StepId,
            Order = 3,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Source = new NyxIdChatStepSource
            {
                Llm = new NyxIdChatLLMStepSource
                {
                    ActionRequestId = actionRequestId,
                },
            },
            DependsOn = { postconditionStepId },
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = continuationKey,
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
            },
        });
        state.RecentActions.Add(new NyxIdChatActionRequestState
        {
            SchemaVersion = 4,
            ConversationActorId = conversationActorId,
            OriginTurnId = originTurnId,
            TaskId = taskId,
            StepId = "step-browser-action-alpha",
            SourceToolStepId = "step-source-tool-alpha",
            ActionRequestId = actionRequestId,
            Action = NyxIdAssistantActionKind.ServiceAccessReview,
            Params = new NyxIdAssistantActionParams
            {
                ServiceAccessReview = new NyxIdServiceAccessReviewParams
                {
                    UserServiceId = "us-alpha",
                    ServiceSlug = "service-alpha",
                    ResourceUri = "https://service.invalid/resource",
                },
            },
            PostconditionResult = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = actionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = "us-alpha",
                    },
                },
            },
        });
        return state;
    }

    private static AgentToolOperationAdmissionPayload CreateConnectedServiceReadAdmission(
        string userServiceId,
        string serviceSlug) =>
        new()
        {
            ServiceInstanceId = userServiceId,
            ServiceSlug = serviceSlug,
            PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
            {
                EndpointId = "endpoint-read-alpha",
            },
            AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
            HttpMethod = "GET",
            PathTemplate = "/items",
            ContractDigest = new string('d', 64),
            CatalogDigest = $"sha256:{new string('c', 64)}",
            ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
            {
                Risk = AgentToolOperationRiskPayload.ReadOnly,
                Approval = AgentToolOperationApprovalPayload.None,
                EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
                AllowedExecutionModes =
                {
                    AgentToolOperationExecutionModePayload.Interactive,
                },
            },
        };

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
        AgentTurnToolCatalogMaterializer? turnCatalogMaterializer = null,
        TimeProvider? timeProvider = null,
        INyxIdChatTurnIntentClassifier? turnIntentClassifier = null)
    {
        var operations = new List<string>();
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            actorDispatchPort ??
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            timeProvider ?? new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)),
            turnCatalogMaterializer,
            turnIntentClassifier: turnIntentClassifier)
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private class DelegatingSecretVault(ISecretVault inner) : ISecretVault
    {
        protected ISecretVault Inner { get; } = inner;

        public virtual Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default) => Inner.PutAsync(request, ct);

        public virtual Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default) => Inner.ResolveAsync(request, ct);

        public virtual Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) => Inner.RotateAsync(request, ct);

        public virtual Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default) => Inner.RevokeAsync(request, ct);
    }

    private sealed class TransientResolveSecretVault(ISecretVault inner)
        : DelegatingSecretVault(inner)
    {
        public bool FailResolution { get; set; }

        public override Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default) =>
            FailResolution
                ? Task.FromException<ResolveSecretResult>(
                    new InvalidOperationException("vault temporarily unavailable"))
                : base.ResolveAsync(request, ct);
    }

    private sealed class CountingSecretVault(ISecretVault inner)
        : DelegatingSecretVault(inner)
    {
        public int PutCount { get; private set; }

        public override Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default)
        {
            PutCount++;
            return base.PutAsync(request, ct);
        }
    }

    private sealed class FailTurnStartEventStore(IEventStore inner) : IEventStore
    {
        public bool FailTurnStart { get; set; }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var batch = events.ToList();
            if (FailTurnStart && batch.Any(evt =>
                    evt.EventData.Is(NyxIdChatTurnStartedEvent.Descriptor)))
            {
                throw new InvalidOperationException("turn start commit unavailable");
            }

            return inner.AppendAsync(agentId, batch, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) => inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(
            string agentId,
            CancellationToken ct = default) => inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) => inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private static ServiceProvider BuildEventSourcingServices(
        IEventStore eventStore,
        IChatHistoryCommandPort? historyCommandPort = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null,
        NyxIdAssistantActionRegistry? actionRegistry = null,
        IGAgentActorRegistryCommandPort? registryCommandPort = null,
        ISecretVault? secretVault = null,
        bool registerSecretVault = true)
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
        if (registerSecretVault)
        {
            services.AddSingleton<ISecretVault>(secretVault ?? new InMemorySecretVault(
                new FixedTimeProvider(
                    new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))));
        }
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

    private sealed class ProfileTaskThenIntentClassifier(string profileIntentId)
        : IAgentProfileTurnClassifier
    {
        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var intentId = request.Candidates.Any(candidate =>
                string.Equals(
                    candidate.IntentId,
                    AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId,
                    StringComparison.Ordinal))
                ? AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId
                : profileIntentId;
            return Task.FromResult(AgentProfileTurnClassificationResult.Matched(intentId));
        }
    }

    private sealed class ExactConnectedServiceRoutingClassifierProvider(string profileIntentId)
        : ILLMProvider
    {
        public string Name => "exact-connected-service-routing-classifier-test";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            using var input = JsonDocument.Parse(request.Messages
                .Single(static message => message.Role == "user").Content!);
            var root = input.RootElement;
            root.GetProperty("user_message").GetString().Should()
                .Contain("exact UserService");
            var intents = root.GetProperty("intents");
            var intentIds = intents.EnumerateArray()
                .Select(static intent => intent.GetProperty("intent_id").GetString())
                .ToArray();
            var phaseOne = intentIds.Contains(
                AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId,
                StringComparer.Ordinal);
            if (phaseOne)
            {
                intents.EnumerateArray().Single(intent => string.Equals(
                        intent.GetProperty("intent_id").GetString(),
                        NyxIdChatTurnIntentClassifier.ServiceConnectIntentId,
                        StringComparison.Ordinal))
                    .GetProperty("routing_description").GetString().Should()
                    .Contain("Do not select this intent");
                intents.EnumerateArray().Single(intent => string.Equals(
                        intent.GetProperty("intent_id").GetString(),
                        AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId,
                        StringComparison.Ordinal))
                    .GetProperty("routing_description").GetString().Should()
                    .Contain("already-connected exact UserService");
            }

            yield return new LLMStreamChunk
            {
                DeltaContent = JsonSerializer.Serialize(new
                {
                    status = "matched",
                    intent_id = phaseOne
                        ? AgentTurnToolCatalogMaterializer.ProfileTaskRouteIntentId
                        : profileIntentId,
                }),
                IsLast = true,
            };
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingTurnIntentClassifier(NyxIdChatTurnIntent result)
        : INyxIdChatTurnIntentClassifier
    {
        public List<string> RequestIds { get; } = [];
        public List<string> UserMessages { get; } = [];
        public List<LLMControlContext?> LlmControls { get; } = [];

        public Task<NyxIdChatTurnIntent> ClassifyAsync(
            string requestId,
            string userMessage,
            LLMControlContext? llmControl,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RequestIds.Add(requestId);
            UserMessages.Add(userMessage);
            LlmControls.Add(llmControl);
            return Task.FromResult(result);
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
        public string ParametersSchema => """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["service_slug", "requested_scopes"],
              "properties": {
                "service_slug": { "type": "string" },
                "requested_scopes": {
                  "type": "array",
                  "items": { "type": "string" }
                }
              }
            }
            """;
        public bool IsReadOnly => true;
        public int ExecutionCount { get; private set; }
        public string? ArgumentsJson { get; private set; }
        public string? SourceReadableBearerToken { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionCount++;
            ArgumentsJson = argumentsJson;
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
                    RequestedScopes = { "repo" },
                },
            };
    }

    private sealed class VerifiedServiceCatalogTool : IAgentTool
    {
        public string Name => "nyxid_catalog";
        public string Description => "Resolve an exact NyxID catalog service slug.";
        public string ParametersSchema =>
            "{\"type\":\"object\",\"properties\":{\"slug\":{\"type\":\"string\"}}}";
        public bool IsReadOnly => true;
        public int ExecutionCount { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(
                "{\"slug\":\"aws-cost-explorer\",\"name\":\"AWS Cost Explorer\"}");
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
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
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
            var round = Requests.Count;
            Requests.Add(request);
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = round == 0
                        ? "call-catalog-aws-cost-explorer"
                        : "call-require-aws-cost-explorer",
                    Name = round == 0 ? "nyxid_catalog" : "nyxid_require_service",
                    ArgumentsJson = round == 0
                        ? "{\"slug\":\"aws-cost-explorer\"}"
                        : "{\"service_slug\":\"aws-cost-explorer\",\"requested_scopes\":[\"repo\"]}",
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

    private sealed class PinnedClassRToolCallProvider(
        IReadOnlyList<string> toolNames) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "pinned-class-r-test";
        public List<LLMRequest> Requests { get; } = [];

        public ILLMProvider GetProvider(string name) => this;
        public ILLMProvider GetDefault() => this;
        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var round = Requests.Count;
            Requests.Add(request);
            if (round < toolNames.Count)
            {
                var toolName = toolNames[round];
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = $"call-{toolName}",
                        Name = toolName,
                        ArgumentsJson = "{}",
                    },
                };
                await Task.CompletedTask;
                yield return new LLMStreamChunk
                {
                    FinishReason = "tool_calls",
                    IsLast = true,
                };
                yield break;
            }

            yield return new LLMStreamChunk
            {
                DeltaContent = "NyxID account reads completed.",
            };
            await Task.CompletedTask;
            yield return new LLMStreamChunk
            {
                FinishReason = "stop",
                IsLast = true,
            };
        }
    }

    private sealed class RecordingNyxIdReadHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedNyxIdReadRequest> _requests = new();

        public IReadOnlyCollection<RecordedNyxIdReadRequest> Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(new RecordedNyxIdReadRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.Parameter ?? string.Empty));
            var responseJson = request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/users/me" =>
                    "{\"id\":\"user-alpha\",\"email\":\"user@example.test\",\"status\":\"active\"}",
                "/api/v1/keys" => "{\"keys\":[]}",
                "/api/v1/api-keys" => "{\"api_keys\":[]}",
                "/api/v1/nodes" => "{\"nodes\":[]}",
                "/api/v1/sessions" => "{\"sessions\":[]}",
                _ => "{\"error\":\"unexpected_route\"}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private sealed record RecordedNyxIdReadRequest(
        HttpMethod Method,
        string PathAndQuery,
        string BearerToken);

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

    private static NyxIdAssistantActionRegistry CreateLeastScopeActionRegistry() =>
        NyxIdAssistantActionRegistry.Load("""
        {
          "schema_version": 4,
          "revision": "nyxid-assistant-actions.v6",
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
            },
            {
              "action": "key.create",
              "description": "Create a least-scope API key.",
              "params_schema": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "platform", "allowedServiceIds"],
                "properties": {
                  "name": {"type": "string"},
                  "platform": {"type": "string"},
                  "allowedServiceIds": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 64,
                    "uniqueItems": true,
                    "items": {"type": "string"}
                  }
                }
              },
              "risk": "grant",
              "tier": "v1",
              "remember_eligible": false
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

    private static async Task<(
        NyxIdChatSteeringCommand Steering,
        NyxIdChatOperationKey ToolKey,
        NyxIdChatOperationDispatchCommand Verification,
        string ContinuationTurnId)> ArrangePendingEffectSteeringVerificationAsync(
        NyxIdChatConversationGAgent agent,
        IEventStore eventStore,
        RecordingActorDispatchPort dispatch,
        string conversationActorId)
    {
        agent.State.OwnerSubject = "owner-alpha";
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            WithOwner(CreateStartTurnCommand(), "owner-alpha")));
        var llmKey = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        await agent.HandleEventAsync(CreateEnvelope(
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
                            CallId = "call-alpha",
                            ToolName = "repository_update",
                            ArgumentsJson = "{\"repositoryId\":\"repo-alpha\"}",
                            Safety = new NyxIdChatToolCallSafety
                            {
                                SideEffectKind = "repository.update",
                                MayChangeExternalState = true,
                            },
                            NyxIdProvenance = new NyxIdOperationRef
                            {
                                ConnectedServiceId = "svc-repository-alpha",
                                ServiceSlug = "repository-service",
                                OperationId = "repository-update",
                            },
                            OperationAdmission = CreateEffectAdmissionWithReadBack(),
                        },
                    },
                },
            }));
        var toolKey = agent.State.ActiveTask.Steps
            .Single(step => step.Kind == NyxIdChatStepKind.Tool)
            .Operation.Key.Clone();
        var committed = await eventStore.GetEventsAsync(conversationActorId);
        var steering = CreateSteeringCommand(committed[^1].Version);
        steering.ToolContext.Caller = new Aevatar.AI.Abstractions.AgentToolCallerContextPayload
        {
            OwnerSubject = "owner-alpha",
        };
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, steering));
        var continuationTurnId = agent.State.ContinuationAdmission.ContinuationTurnId;
        agent.State.ContinuationAdmission.Status.Should().Be(
            NyxIdChatContinuationAdmissionStatus.AcceptedForLater);

        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = toolKey.Clone(),
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
        var verification = dispatch.OperationCalls[^1].Envelope.Payload
            .Unpack<NyxIdChatOperationDispatchCommand>();
        verification.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification);
        return (steering, toolKey, verification, continuationTurnId);
    }

    private static async Task<EventEnvelope> AcceptPendingSteeringAfterVerificationAsync(
        NyxIdChatConversationGAgent agent,
        (
            NyxIdChatSteeringCommand Steering,
            NyxIdChatOperationKey ToolKey,
            NyxIdChatOperationDispatchCommand Verification,
            string ContinuationTurnId) arranged,
        string conversationActorId)
    {
        var dispatch = (RecordingActorDispatchPort)typeof(NyxIdChatConversationGAgent)
            .GetField("_actorDispatchPort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;
        await agent.HandleEventAsync(CreateEnvelope(
            conversationActorId,
            new NyxIdChatOperationResultSignal
            {
                Key = arranged.Verification.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = arranged.ToolKey.StepId,
                    Disposition = NyxIdChatToolVerificationDisposition.Applied,
                    ReadOperation = arranged.Verification.ToolVerification.ReadBack
                        .ReadOperation.Clone(),
                    CheckName = arranged.Verification.ToolVerification.ReadBack.CheckName,
                },
            }));
        return dispatch.Calls.Last(call => call.Envelope.Payload.Is(
            NyxIdChatPendingSteeringContinuationDispatchRequested.Descriptor)).Envelope.Clone();
    }

    private static AgentToolOperationAdmissionPayload CreateEffectAdmissionWithReadBack()
    {
        var readOperation = new AgentToolOperationAdmissionPayload
        {
            ServiceInstanceId = "svc-repository-alpha",
            ServiceSlug = "repository-service",
            PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
            {
                EndpointId = "repository-read",
            },
            AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
            HttpMethod = "GET",
            PathTemplate = "/repositories/{repositoryId}",
            ContractDigest = new string('d', 64),
            CatalogDigest = $"sha256:{new string('a', 64)}",
            ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
            {
                Risk = AgentToolOperationRiskPayload.ReadOnly,
                Approval = AgentToolOperationApprovalPayload.None,
                EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
                AllowedExecutionModes =
                {
                    AgentToolOperationExecutionModePayload.Interactive,
                },
            },
        };
        return new AgentToolOperationAdmissionPayload
        {
            ServiceInstanceId = "svc-repository-alpha",
            ServiceSlug = "repository-service",
            PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
            {
                EndpointId = "repository-update",
            },
            AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
            HttpMethod = "PATCH",
            PathTemplate = "/repositories/{repositoryId}",
            ContractDigest = new string('b', 64),
            CatalogDigest = $"sha256:{new string('a', 64)}",
            ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
            {
                Risk = AgentToolOperationRiskPayload.Write,
                Approval = AgentToolOperationApprovalPayload.Required,
                EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
                AllowedExecutionModes =
                {
                    AgentToolOperationExecutionModePayload.Interactive,
                },
            },
            ReadBack = new AgentToolOperationReadBackPayload
            {
                ReadOperation = readOperation,
                Arguments = new Struct
                {
                    Fields =
                    {
                        ["repositoryId"] = Google.Protobuf.WellKnownTypes.Value.ForString(
                            "repo-alpha"),
                    },
                },
                CheckName = "repository-visible",
                Assertion = new AgentToolReadBackAssertionPayload
                {
                    Match = AgentToolReadBackMatchPayload.Exists,
                    JsonPointer = "/data",
                },
            },
        };
    }

    private static AgentToolOperationAdmissionPayload CreateConnectedServiceWriteAdmission() =>
        new()
        {
            ServiceInstanceId = "connected-service-lark",
            ServiceSlug = "lark",
            PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
            {
                EndpointId = "lark-message-create",
            },
            AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
            HttpMethod = "POST",
            PathTemplate = "/messages",
            ContractDigest = new string('b', 64),
            CatalogDigest = $"sha256:{new string('a', 64)}",
            ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
            {
                Risk = AgentToolOperationRiskPayload.Write,
                Approval = AgentToolOperationApprovalPayload.Required,
                EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
                AllowedExecutionModes =
                {
                    AgentToolOperationExecutionModePayload.Interactive,
                },
            },
        };

    private static void AssignActorId(
        NyxIdChatConversationGAgent agent,
        string actorId)
    {
        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);
        var dispatchPort = (IActorDispatchPort)typeof(NyxIdChatConversationGAgent)
            .GetField("_actorDispatchPort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;
        agent.EventPublisher = new NyxIdChatTestSelfEventPublisher(actorId, dispatchPort);
    }

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
        public Exception? ScheduleException { get; set; }

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ScheduleException is not null)
                return Task.FromException<RuntimeCallbackLease>(ScheduleException);

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

    private static async Task DispatchPendingCreationFirstTurnAsync(
        NyxIdChatConversationGAgent agent,
        RecordingActorDispatchPort dispatch)
    {
        if (agent.State.PendingHistoryInitialization is not null)
        {
            var historySignal = dispatch.Calls
                .Last(call => call.Envelope.Payload.Is(
                    NyxIdChatHistoryInitializationDispatchRequested.Descriptor))
                .Envelope.Payload
                .Unpack<NyxIdChatHistoryInitializationDispatchRequested>();
            await agent.HandleHistoryInitializationDispatchRequestedAsync(historySignal);
        }

        var signal = dispatch.Calls
            .Last(call => call.Envelope.Payload.Is(
                NyxIdChatPendingCreationFirstTurnDispatchRequested.Descriptor))
            .Envelope.Payload
            .Unpack<NyxIdChatPendingCreationFirstTurnDispatchRequested>();
        await agent.HandlePendingCreationFirstTurnDispatchRequestedAsync(signal);
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
        Func<ChatHistoryTurnDeliveryReservation, Task>? onReserve = null,
        Func<ChatHistoryConversationInitialization, Task>? onInitialize = null)
        : IChatHistoryCommandPort
    {
        public Exception? InitializeException { get; set; }
        public Exception? ReserveException { get; set; }
        public Exception? NotifyException { get; set; }
        public List<ChatHistoryConversationInitialization> Initializations { get; } = [];
        public List<ChatHistoryTurnDeliveryReservation> Reservations { get; } = [];
        public List<ChatHistoryTurnTerminalNotification> Notifications { get; } = [];

        public async Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add("history.initialize");
            Initializations.Add(request);
            if (InitializeException is not null)
                throw InitializeException;
            if (onInitialize is not null)
                await onInitialize(request);
        }

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

internal sealed class NyxIdChatTestSelfEventPublisher(
    string actorId,
    IActorDispatchPort dispatchPort) : IEventPublisher
{
    private static readonly DefaultEnvelopePropagationPolicy PropagationPolicy =
        new(new DefaultCorrelationLinkPolicy());

    public Task PublishAsync<TEvent>(
        TEvent evt,
        TopologyAudience audience = TopologyAudience.Children,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        if (audience != TopologyAudience.Self)
            throw new NotSupportedException("The test publisher only supports self publication.");

        return DispatchAsync(
            actorId,
            evt,
            EnvelopeRouteSemantics.CreateTopologyPublication(actorId, audience),
            sourceEnvelope,
            options,
            ct);
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        if (!string.Equals(targetActorId, actorId, StringComparison.Ordinal))
            throw new NotSupportedException("The test publisher only supports direct self delivery.");

        return DispatchAsync(
            targetActorId,
            evt,
            EnvelopeRouteSemantics.CreateDirect(actorId, targetActorId),
            sourceEnvelope,
            options,
            ct);
    }

    private Task DispatchAsync(
        string targetActorId,
        IMessage evt,
        EnvelopeRoute route,
        EventEnvelope? sourceEnvelope,
        EventEnvelopePublishOptions? options,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = route,
        };
        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            envelope,
            sourceEnvelope,
            PropagationPolicy,
            actorId,
            routeTargetCount: 1,
            options);
        return dispatchPort.DispatchAsync(targetActorId, envelope, ct);
    }
}
