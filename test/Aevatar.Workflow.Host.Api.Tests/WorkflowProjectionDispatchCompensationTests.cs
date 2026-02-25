using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
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

    private static ProjectionCompensationEnqueuedEvent CreateEnqueueEvent(string recordId, string readModelJson) =>
        new()
        {
            RecordId = recordId,
            Operation = "mutate",
            FailedStore = "Graph",
            SucceededStores = { "Document" },
            ReadModelType = typeof(WorkflowExecutionReport).FullName!,
            ReadModelJson = readModelJson,
            Key = recordId,
            LastError = "test",
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

        public FlakyGraphBinding(int failuresBeforeSuccess)
        {
            _remainingFailures = Math.Max(0, failuresBeforeSuccess);
        }

        public string StoreName => "Graph";

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
