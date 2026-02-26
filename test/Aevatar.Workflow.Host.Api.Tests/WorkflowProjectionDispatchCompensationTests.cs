using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowProjectionDispatchCompensationOutboxGAgentTests
{
    private static IServiceProvider CreateAgentServices(
        IEventStore? eventStore = null,
        IEnumerable<IProjectionStoreBinding<WorkflowExecutionReport, string>>? bindings = null,
        WorkflowExecutionProjectionOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventStore ?? new InMemoryEventStore());
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton(options ?? new WorkflowExecutionProjectionOptions
        {
            DispatchCompensationReplayBaseDelayMs = 0,
            DispatchCompensationReplayMaxDelayMs = 0,
        });
        foreach (var binding in bindings ?? [])
            services.AddSingleton(binding);

        return services.BuildServiceProvider();
    }

    private static WorkflowProjectionDispatchCompensationOutboxGAgent CreateAgent(IServiceProvider services) =>
        new()
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ProjectionDispatchCompensationOutboxState>>(),
        };

    [Fact]
    public async Task HandleEnqueue_ShouldAddEntryToState()
    {
        var agent = CreateAgent(CreateAgentServices());

        await agent.HandleEnqueueAsync(new ProjectionCompensationEnqueuedEvent
        {
            RecordId = "r1",
            Operation = "mutate",
            FailedStore = "Graph",
            SucceededStores = { "Document" },
            ReadModelType = typeof(WorkflowExecutionReport).FullName!,
            ReadModelJson = "{}",
            Key = "root-1",
            LastError = "InvalidOperationException",
        });

        agent.State.Entries.Should().ContainKey("r1");
        var entry = agent.State.Entries["r1"];
        entry.FailedStore.Should().Be("Graph");
        entry.Operation.Should().Be("mutate");
        entry.AttemptCount.Should().Be(0);
        entry.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task HandleEnqueue_ShouldThrow_WhenRecordIdMissing()
    {
        var agent = CreateAgent(CreateAgentServices());

        Func<Task> act = () => agent.HandleEnqueueAsync(new ProjectionCompensationEnqueuedEvent
        {
            FailedStore = "Graph",
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task HandleEnqueue_ShouldThrow_WhenFailedStoreMissing()
    {
        var agent = CreateAgent(CreateAgentServices());

        Func<Task> act = () => agent.HandleEnqueueAsync(new ProjectionCompensationEnqueuedEvent
        {
            RecordId = "r1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void BuildActorId_ShouldTrimScopeAndThrowForBlank()
    {
        WorkflowProjectionDispatchCompensationOutboxGAgent.BuildActorId(" workflow ").Should()
            .Be("projection.compensation.outbox:workflow");

        Action act = () => WorkflowProjectionDispatchCompensationOutboxGAgent.BuildActorId("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task HandleTriggerReplay_WithSuccessfulBinding_ShouldMarkSucceeded()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 0);
        var services = CreateAgentServices(bindings: [binding]);
        var agent = CreateAgent(services);

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", "{}"));
        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });

        agent.State.Entries["r1"].CompletedAtUtc.Should().NotBeNull();
        binding.AttemptCount.Should().Be(1);
        binding.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleTriggerReplay_WithFlakyBinding_ShouldRetryThenSucceed()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 1);
        var services = CreateAgentServices(bindings: [binding]);
        var agent = CreateAgent(services);

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", CreateReadModelJson("root-1")));

        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });
        binding.AttemptCount.Should().Be(1);
        binding.SuccessCount.Should().Be(0);
        agent.State.Entries["r1"].AttemptCount.Should().Be(1);
        agent.State.Entries["r1"].CompletedAtUtc.Should().BeNull();

        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });
        binding.AttemptCount.Should().Be(2);
        binding.SuccessCount.Should().Be(1);
        agent.State.Entries["r1"].CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleTriggerReplay_ShouldRespectBatchSize()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 0);
        var services = CreateAgentServices(bindings: [binding]);
        var agent = CreateAgent(services);

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", "{}"));
        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r2", "{}"));
        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r3", "{}"));

        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 2 });
        binding.AttemptCount.Should().Be(2);

        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 2 });
        binding.AttemptCount.Should().Be(3);
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenBatchSizeIsNonPositive_ShouldFallbackToDefaultBatchSize()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 0);
        var services = CreateAgentServices(bindings: [binding]);
        var agent = CreateAgent(services);

        for (var i = 0; i < 21; i++)
            await agent.HandleEnqueueAsync(CreateEnqueueEvent($"r{i}", CreateReadModelJson($"root-{i}")));

        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 0 });
        binding.AttemptCount.Should().Be(20);

        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = -1 });
        binding.AttemptCount.Should().Be(21);
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenBindingMissing_ShouldScheduleRetry()
    {
        var agent = CreateAgent(CreateAgentServices());

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", CreateReadModelJson("root-1")));
        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });

        var entry = agent.State.Entries["r1"];
        entry.AttemptCount.Should().Be(1);
        entry.CompletedAtUtc.Should().BeNull();
        entry.LastError.Should().Contain("not registered");
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenReadModelJsonInvalid_ShouldScheduleRetryWithoutStoreWrite()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 0);
        var services = CreateAgentServices(bindings: [binding]);
        var agent = CreateAgent(services);

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", "{"));
        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });

        var entry = agent.State.Entries["r1"];
        entry.AttemptCount.Should().Be(1);
        entry.LastError.Should().Contain("JsonException");
        binding.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenReadModelJsonDeserializesNull_ShouldScheduleRetry()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 0);
        var services = CreateAgentServices(bindings: [binding]);
        var agent = CreateAgent(services);

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", "null"));
        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });

        var entry = agent.State.Entries["r1"];
        entry.AttemptCount.Should().Be(1);
        entry.LastError.Should().Contain("deserialized null read model");
        binding.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenEntryNotVisibleYet_ShouldSkipReplay()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 0);
        var services = CreateAgentServices(bindings: [binding]);
        var agent = CreateAgent(services);
        var futureVisibleAt = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1));

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", CreateReadModelJson("root-1"), enqueuedAtUtc: futureVisibleAt));
        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });

        binding.AttemptCount.Should().Be(0);
        var entry = agent.State.Entries["r1"];
        entry.AttemptCount.Should().Be(0);
        entry.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenBindingsDuplicate_ShouldUseLastRegisteredStoreBinding()
    {
        var first = new FlakyGraphBinding(failuresBeforeSuccess: 100, storeName: "Graph");
        var second = new FlakyGraphBinding(failuresBeforeSuccess: 0, storeName: "graph");
        var services = CreateAgentServices(bindings: [first, second]);
        var agent = CreateAgent(services);

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", CreateReadModelJson("root-1")));
        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });

        first.AttemptCount.Should().Be(0);
        second.AttemptCount.Should().Be(1);
        agent.State.Entries["r1"].CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleTriggerReplay_WhenBaseDelayConfigured_ShouldScheduleFutureVisibility()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 1);
        var options = new WorkflowExecutionProjectionOptions
        {
            DispatchCompensationReplayBaseDelayMs = 200,
            DispatchCompensationReplayMaxDelayMs = 1000,
        };
        var services = CreateAgentServices(bindings: [binding], options: options);
        var agent = CreateAgent(services);
        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", CreateReadModelJson("root-1")));

        await agent.HandleTriggerReplayAsync(new ProjectionCompensationTriggerReplayEvent { BatchSize = 10 });
        var afterReplay = DateTime.UtcNow;

        var entry = agent.State.Entries["r1"];
        entry.AttemptCount.Should().Be(1);
        entry.NextVisibleAtUtc.Should().NotBeNull();
        var nextVisible = entry.NextVisibleAtUtc!.ToDateTime();
        if (nextVisible.Kind != DateTimeKind.Utc)
            nextVisible = DateTime.SpecifyKind(nextVisible, DateTimeKind.Utc);
        nextVisible.Should().BeAfter(afterReplay);
    }

    [Fact]
    public async Task State_ShouldSurviveDeactivateAndReactivate()
    {
        var store = new InMemoryEventStore();
        var services = CreateAgentServices(eventStore: store);

        var agent1 = CreateAgent(services);
        await agent1.ActivateAsync();
        await agent1.HandleEnqueueAsync(CreateEnqueueEvent("r1", "{}"));
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(services);
        await agent2.ActivateAsync();

        agent2.State.Entries.Should().ContainKey("r1");
        agent2.State.Entries["r1"].FailedStore.Should().Be("Graph");
    }

    [Fact]
    public async Task CompensatorIntegration_ShouldEnqueueViaActor()
    {
        var services = CreateAgentServices();
        var agent = CreateAgent(services);

        var outbox = new DirectOutbox(agent);
        var compensator = new WorkflowProjectionDurableOutboxCompensator(outbox);
        var context = new ProjectionStoreDispatchCompensationContext<WorkflowExecutionReport, string>
        {
            Operation = "mutate",
            Key = "root-1",
            ReadModel = CreateReadModel("root-1"),
            FailedStore = "Graph",
            SucceededStores = ["Document"],
            Exception = new InvalidOperationException("graph write failed"),
        };

        await compensator.CompensateAsync(context);

        agent.State.Entries.Should().HaveCount(1);
        var entry = agent.State.Entries.Values.Single();
        entry.FailedStore.Should().Be("Graph");
        entry.Operation.Should().Be("mutate");
        entry.ReadModelJson.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ThinTimerIntegration_ShouldTriggerReplayViaOutbox()
    {
        var binding = new FlakyGraphBinding(failuresBeforeSuccess: 0);
        var services = CreateAgentServices(bindings: [binding]);
        var agent = CreateAgent(services);

        await agent.HandleEnqueueAsync(CreateEnqueueEvent("r1", CreateReadModelJson("root-1")));

        var outbox = new DirectOutbox(agent);
        var options = new WorkflowExecutionProjectionOptions
        {
            DispatchCompensationReplayBatchSize = 10,
        };
        var timer = new WorkflowProjectionDispatchCompensationReplayHostedService(outbox, options);

        await timer.ReplayOnceAsync();

        agent.State.Entries["r1"].CompletedAtUtc.Should().NotBeNull();
        binding.SuccessCount.Should().Be(1);
    }

    private static ProjectionCompensationEnqueuedEvent CreateEnqueueEvent(
        string recordId,
        string readModelJson,
        string failedStore = "Graph",
        Timestamp? enqueuedAtUtc = null) =>
        new()
        {
            RecordId = recordId,
            Operation = "mutate",
            FailedStore = failedStore,
            SucceededStores = { "Document" },
            ReadModelType = typeof(WorkflowExecutionReport).FullName!,
            ReadModelJson = readModelJson,
            Key = recordId,
            LastError = "test",
            EnqueuedAtUtc = enqueuedAtUtc,
        };

    private static string CreateReadModelJson(string rootActorId) =>
        System.Text.Json.JsonSerializer.Serialize(CreateReadModel(rootActorId));

    private static WorkflowExecutionReport CreateReadModel(string rootActorId) =>
        new()
        {
            Id = rootActorId,
            RootActorId = rootActorId,
            CommandId = "command-1",
            WorkflowName = "workflow-1",
            Input = "input",
            StartedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class FlakyGraphBinding : IProjectionStoreBinding<WorkflowExecutionReport, string>
    {
        private int _remainingFailures;
        private readonly string _storeName;

        public FlakyGraphBinding(int failuresBeforeSuccess, string storeName = "Graph")
        {
            _remainingFailures = Math.Max(0, failuresBeforeSuccess);
            _storeName = storeName;
        }

        public string StoreName => _storeName;

        public int AttemptCount { get; private set; }

        public int SuccessCount { get; private set; }

        public Task UpsertAsync(WorkflowExecutionReport readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(readModel);

            AttemptCount++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new InvalidOperationException("Simulated graph write failure.");
            }

            SuccessCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Bypasses IActorRuntime by dispatching events directly to the GAgent for unit testing.
    /// </summary>
    private sealed class DirectOutbox : IProjectionDispatchCompensationOutbox
    {
        private readonly WorkflowProjectionDispatchCompensationOutboxGAgent _agent;

        public DirectOutbox(WorkflowProjectionDispatchCompensationOutboxGAgent agent)
        {
            _agent = agent;
        }

        public Task EnqueueAsync(ProjectionCompensationEnqueuedEvent evt, CancellationToken ct = default) =>
            _agent.HandleEnqueueAsync(evt);

        public Task TriggerReplayAsync(int batchSize, CancellationToken ct = default) =>
            _agent.HandleTriggerReplayAsync(
                new ProjectionCompensationTriggerReplayEvent { BatchSize = batchSize });
    }
}
