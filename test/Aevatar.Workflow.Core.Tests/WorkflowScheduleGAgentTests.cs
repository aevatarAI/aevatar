using System.Reflection;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowScheduleGAgentTests
{
    [Fact]
    public async Task HandleFireAsync_ShouldSuppressDuplicateDispatchAfterStartedRecordIsDurable()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingWorkflowRunDispatchService();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(new WorkflowScheduleConfigureCommand
        {
            ScheduleId = "schedule-1",
            WorkflowName = "direct",
            Prompt = "hello",
            CronExpression = "*/15 * * * *",
            Timezone = "UTC",
            Enabled = false,
        });

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new WorkflowScheduleFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });
        await agent.HandleFireAsync(new WorkflowScheduleFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        dispatch.Commands.Should().ContainSingle();
        var idempotencyKey = WorkflowScheduleCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(WorkflowScheduleFireStatusState.Dispatched);
    }

    private static WorkflowScheduleGAgent CreateAgent(
        IEventStore eventStore,
        RecordingWorkflowRunDispatchService dispatch)
    {
        var agent = new WorkflowScheduleGAgent(dispatch)
        {
            Services = new TestServiceProvider(),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowScheduleState>(eventStore),
        };
        SetAgentId(agent, "workflow-schedule:schedule-1");
        return agent;
    }

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private sealed class RecordingWorkflowRunDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(
                CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                    new WorkflowChatRunAcceptedReceipt("run-actor-1", "direct", "cmd-1", "corr-1")));
        }
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();

            return null;
        }
    }

    private sealed class TestEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _streams = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            currentVersion.Should().Be(expectedVersion);

            var committed = events.Select(x => x.Clone()).ToList();
            stream.AddRange(committed);
            _streams[agentId] = stream;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult<IReadOnlyList<StateEvent>>(
                events.Where(x => !fromVersion.HasValue || x.Version >= fromVersion.Value)
                    .Select(x => x.Clone())
                    .ToArray());
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult(stream.Count == 0 ? 0 : stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0L);
        }
    }

}
