using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.PrimitiveExecutors;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SystemType = System.Type;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowTuringCompleteness")]
public sealed class WorkflowTuringCompletenessTests
{
    [Fact]
    public async Task IncDecJzProgram_ShouldTransferCounterValueInClosedWorldMode()
    {
        var yaml = BuildCounterTransferWorkflowYaml();
        WorkflowValidator.Validate(new WorkflowParser().Parse(yaml)).Should().BeEmpty();

        var completed = await ExecuteClosedWorldWorkflowAsync(yaml, maxSelfEvents: 256);

        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("2");
    }

    [Fact]
    public async Task TwoCounterProgram_ShouldComputeAdditionInClosedWorldMode()
    {
        var yaml = BuildCounterAdditionWorkflowYaml();
        WorkflowValidator.Validate(new WorkflowParser().Parse(yaml)).Should().BeEmpty();

        var completed = await ExecuteClosedWorldWorkflowAsync(yaml, maxSelfEvents: 512);

        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("5");
    }

    [Fact]
    public async Task NonHaltingProgram_ShouldExceedTransitionBudget()
    {
        var yaml = BuildNonHaltingWorkflowYaml();
        WorkflowValidator.Validate(new WorkflowParser().Parse(yaml)).Should().BeEmpty();

        Func<Task> run = async () => await ExecuteClosedWorldWorkflowAsync(yaml, maxSelfEvents: 64);
        await run.Should().ThrowAsync<TimeoutException>();
    }

    private static string BuildCounterTransferWorkflowYaml() =>
        """
        name: counter_transfer
        configuration:
          closed_world_mode: true
        roles: []
        steps:
          - id: init_c1
            type: assign
            parameters:
              target: c1
              value: "2"
          - id: init_c2
            type: assign
            parameters:
              target: c2
              value: "0"
          - id: check_c1
            type: conditional
            parameters:
              condition: "${eq(variables.c1, '0')}"
            branches:
              - condition: "true"
                next: halt
              - condition: "false"
                next: dec_c1
          - id: dec_c1
            type: assign
            next: inc_c2
            parameters:
              target: c1
              value: "${sub(variables.c1, 1)}"
          - id: inc_c2
            type: assign
            next: check_c1
            parameters:
              target: c2
              value: "${add(variables.c2, 1)}"
          - id: halt
            type: assign
            parameters:
              target: result
              value: "${variables.c2}"
        """;

    private static string BuildCounterAdditionWorkflowYaml() =>
        """
        name: counter_addition
        configuration:
          closed_world_mode: true
        roles: []
        steps:
          - id: init_a
            type: assign
            parameters:
              target: a
              value: "2"
          - id: init_b
            type: assign
            parameters:
              target: b
              value: "3"
          - id: check_b
            type: conditional
            parameters:
              condition: "${eq(variables.b, '0')}"
            branches:
              - condition: "true"
                next: halt
              - condition: "false"
                next: inc_a
          - id: inc_a
            type: assign
            next: dec_b
            parameters:
              target: a
              value: "${add(variables.a, 1)}"
          - id: dec_b
            type: assign
            next: check_b
            parameters:
              target: b
              value: "${sub(variables.b, 1)}"
          - id: halt
            type: assign
            parameters:
              target: result
              value: "${variables.a}"
        """;

    private static string BuildNonHaltingWorkflowYaml() =>
        """
        name: non_halting
        configuration:
          closed_world_mode: true
        roles: []
        steps:
          - id: loop
            type: conditional
            parameters:
              condition: "false"
            branches:
              - condition: "true"
                next: halt
              - condition: "false"
                next: loop
          - id: halt
            type: assign
            parameters:
              target: result
              value: "done"
        """;

    private static async Task<WorkflowCompletedEvent> ExecuteClosedWorldWorkflowAsync(string workflowYaml, int maxSelfEvents)
    {
        var eventStore = new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp => sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new WorkflowRunGAgent(
            new NullActorRuntime(),
            new StaticRoleAgentTypeResolver(typeof(ClosedWorldRoleAgent)),
            [new ClosedWorldPrimitivePack()])
        {
            Services = services,
        };
        AssignAgentId(agent, "workflow-turing-proof-agent");
        agent.EventSourcingBehaviorFactory =
            services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowRunState>>();

        var parsed = new WorkflowParser().Parse(workflowYaml);
        await agent.BindWorkflowDefinitionAsync(workflowYaml, parsed.Name);

        var publisher = new LoopbackEventPublisher(agent);
        agent.EventPublisher = publisher;

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "seed",
            SessionId = "proof-run",
        });

        await publisher.DrainAsync(maxSelfEvents);

        return publisher.ExternalPublished.OfType<WorkflowCompletedEvent>().Single();
    }

    private static void AssignAgentId(IAgent agent, string id)
    {
        var method = agent.GetType().BaseType?.GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(agent, [id]);
    }

    private sealed class ClosedWorldPrimitivePack : IWorkflowPrimitivePack
    {
        public string Name => "closed-world";

        public IReadOnlyList<WorkflowPrimitiveRegistration> Executors { get; } =
        [
            WorkflowPrimitiveRegistration.Create<AssignPrimitiveExecutor>("assign"),
            WorkflowPrimitiveRegistration.Create<ConditionalPrimitiveExecutor>("conditional"),
            WorkflowPrimitiveRegistration.Create<SwitchPrimitiveExecutor>("switch"),
            WorkflowPrimitiveRegistration.Create<TransformPrimitiveExecutor>("transform"),
        ];
    }

    private sealed class LoopbackEventPublisher(WorkflowRunGAgent agent) : IEventPublisher
    {
        private readonly Queue<IMessage> _selfQueue = new();

        public List<IMessage> ExternalPublished { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (direction == EventDirection.Self)
            {
                _selfQueue.Enqueue(evt);
                return Task.CompletedTask;
            }

            ExternalPublished.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage =>
            throw new NotSupportedException($"Closed-world proof does not support SendToAsync ({targetActorId}).");

        public async Task DrainAsync(int maxSelfEvents)
        {
            var processed = 0;
            while (_selfQueue.Count > 0)
            {
                if (processed++ >= maxSelfEvents)
                    throw new TimeoutException($"Workflow did not complete within self-event budget ({maxSelfEvents}).");

                var next = _selfQueue.Dequeue();
                await agent.HandleEventAsync(new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(next),
                    PublisherId = agent.Id,
                    Direction = EventDirection.Self,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                });
            }
        }
    }

    private sealed class NullActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(SystemType agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ClosedWorldRoleAgent : IRoleAgent
    {
        public string Id => "closed-world-role";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("closed-world-role");

        public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<SystemType>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StaticRoleAgentTypeResolver(SystemType roleAgentType) : IRoleAgentTypeResolver
    {
        public SystemType ResolveRoleAgentType() => roleAgentType;
    }
}
