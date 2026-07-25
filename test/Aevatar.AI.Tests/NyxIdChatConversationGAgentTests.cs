using System.Reflection;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Type = System.Type;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatConversationGAgentTests
{
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
        var dispatchPort = new RecordingActorDispatchPort(
            operations,
            async (actorId, envelope) =>
            {
                actorId.Should().Be(NyxIdChatTurnActorIds.ForTurn(conversationActorId, "turn-alpha"));
                stateObservedAtDispatch = agent!.State.Clone();
                eventsObservedAtDispatch = await eventStore.GetEventsAsync(conversationActorId);
                envelope.Payload.Is(NyxIdChatOperationDispatchCommand.Descriptor).Should().BeTrue();
            });
        using var services = BuildEventSourcingServices(eventStore);
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

        operations.Should().Equal("create", "link", "dispatch");
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

        eventsObservedAtDispatch.Should().ContainSingle();
        eventsObservedAtDispatch![0].EventData.Is(NyxIdChatTurnStartedEvent.Descriptor).Should().BeTrue();
        var committedEvents = await eventStore.GetEventsAsync(conversationActorId);
        committedEvents.Select(static item => item.EventData.TypeUrl).Should().Equal(
            Any.Pack(new NyxIdChatTurnStartedEvent()).TypeUrl,
            Any.Pack(new NyxIdChatOperationDispatchedEvent()).TypeUrl);
        agent.State.ActiveTask.Steps.Single().Operation.Phase.Should()
            .Be(NyxIdChatOperationPhase.Dispatched);
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
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var mismatched = new NyxIdChatOperationResultSignal
        {
            Key = key.Clone(),
            Llm = new NyxIdChatLLMOperationResult { Content = "must be ignored" },
        };
        mismatched.Key.OperationId = "operation-wrong";

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, mismatched));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(2);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Active);
        agent.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Running);

        var accepted = new NyxIdChatOperationResultSignal
        {
            Key = key,
            Llm = new NyxIdChatLLMOperationResult { Content = "completed" },
        };
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, accepted));

        var committed = await eventStore.GetEventsAsync(conversationActorId);
        committed.Should().HaveCount(3);
        committed[^1].EventData.Is(NyxIdChatOperationReconciledEvent.Descriptor).Should().BeTrue();
        var reconciliation = committed[^1].EventData.Unpack<NyxIdChatOperationReconciledEvent>();
        reconciliation.Result.Key.Should().BeEquivalentTo(key);
        reconciliation.Task.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        reconciliation.Turn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        agent.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        agent.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Done);
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
        var key = agent.State.ActiveTask.Steps.Single().Operation.Key.Clone();
        var progress = new NyxIdChatOperationProgressSignal
        {
            Key = key,
            Sequence = 1,
            Text = new NyxIdChatTextProgress { Delta = "hello" },
        };

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, progress));

        var afterFirst = await eventStore.GetEventsAsync(conversationActorId);
        afterFirst.Should().HaveCount(3);
        afterFirst[^1].EventData.TypeUrl.Should().EndWith("NyxIdChatOperationProgressedEvent");
        agent.State.ProgressSequence.Should().Be(2);

        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, progress.Clone()));
        var wrong = progress.Clone();
        wrong.Sequence = 2;
        wrong.Key.StepId = "step-wrong";
        await agent.HandleEventAsync(CreateEnvelope(conversationActorId, wrong));

        (await eventStore.GetEventsAsync(conversationActorId)).Should().HaveCount(3);
        agent.State.ProgressSequence.Should().Be(2);
    }

    [Fact]
    public void StreamingCommand_ShouldKeepClientRequestIdentityDistinctFromTurnIdentity()
    {
        typeof(NyxIdChatCommand).GetProperty("ClientRequestId").Should().NotBeNull();
    }

    [Fact]
    public void StreamingEnvelope_ShouldDispatchTypedStartTurnCommand()
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

    private static NyxIdChatConversationGAgent CreateController(
        ServiceProvider services,
        string actorId)
    {
        var operations = new List<string>();
        var agent = new NyxIdChatConversationGAgent(
            new RecordingActorRuntime(operations),
            new RecordingActorDispatchPort(operations, static (_, _) => Task.CompletedTask),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero)))
        {
            Services = services,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<NyxIdChatConversationGAgentState>>(),
        };
        AssignActorId(agent, actorId);
        return agent;
    }

    private static ServiceProvider BuildEventSourcingServices(IEventStore eventStore) =>
        new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopRuntimeCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

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

    private sealed class RecordingActorRuntime(List<string> operations) : IActorRuntime
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
            LinkCalls.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort(
        List<string> operations,
        Func<string, EventEnvelope, Task> onDispatch)
        : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add("dispatch");
            Calls.Add((actorId, envelope.Clone()));
            await onDispatch(actorId, envelope);
            return DispatchAdmissionFactory.Create(actorId, envelope);
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
        public Task<string> GetDescriptionAsync() => Task.FromResult(Id);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
