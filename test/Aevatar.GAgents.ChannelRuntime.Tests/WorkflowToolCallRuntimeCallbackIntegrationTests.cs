using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class WorkflowToolCallRuntimeCallbackIntegrationTests
{
    [Fact]
    public async Task ScheduleTimeoutAsync_WithProductionScaleToolContinuationEvents_ShouldPersistDistinctCallbacks()
    {
        await using var harness = await RuntimeCallbackSchedulerGrainTestHarness.StartAsync();
        var actorId = BuildProductionScaleRunId();
        var stepId = $"extract_documents_execution_{new string('b', 16)}_item_0";
        var leases = new List<RuntimeCallbackLease>();

        foreach (var (payload, executionId) in BuildContinuationEvents(actorId, stepId))
        {
            var callId = $"workflow:{actorId}:{stepId}:{executionId}";
            var callbackId = RuntimeCallbackKeyComposer.BuildCallbackId(
                $"workflow-tool-{payload.Descriptor.Name}",
                actorId,
                stepId,
                callId,
                executionId);

            executionId.Length.Should().BeGreaterThan(350);
            callbackId.Length.Should().BeGreaterThan(1_400);
            leases.Add(await harness.Scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
            {
                ActorId = actorId,
                CallbackId = callbackId,
                DueTime = TimeSpan.FromMinutes(5),
                TriggerEnvelope = new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Payload = Any.Pack(payload),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, TopologyAudience.Self),
                },
            }));
        }

        leases.Should().HaveCount(5);
        leases.Select(static lease => lease.CallbackId).Should().OnlyHaveUniqueItems();
        leases.Should().OnlyContain(static lease =>
            lease.Generation == 1 && lease.Backend == RuntimeCallbackBackend.Dedicated);

        await harness.Scheduler.PurgeActorAsync(actorId);
    }

    private static IReadOnlyList<(IMessage Payload, string ExecutionId)> BuildContinuationEvents(
        string runId,
        string stepId)
    {
        var executionIds = Enumerable.Range(1, 5)
            .Select(index =>
                $"foreach-execution:{runId}:{stepId}:item:{index:D4}:{new string('d', 32)}")
            .ToArray();

        return
        [
            (new WorkflowToolCallTimeoutFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                ExecutionId = executionIds[0],
                CallId = $"workflow:{runId}:{stepId}:{executionIds[0]}",
                TimeoutMs = 300_000,
                ContinuationId = "continuation-1",
            }, executionIds[0]),
            (new WorkflowToolCallRetryFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                ExecutionId = executionIds[1],
                CallId = $"workflow:{runId}:{stepId}:{executionIds[1]}",
                Attempt = 2,
                ContinuationId = "continuation-2",
            }, executionIds[1]),
            (new WorkflowToolCallExecutionRecoveryFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                ExecutionId = executionIds[2],
                CallId = $"workflow:{runId}:{stepId}:{executionIds[2]}",
                Attempt = 1,
                ContinuationId = "continuation-3",
            }, executionIds[2]),
            (new WorkflowToolCallAttemptCompletedEvent
            {
                RunId = runId,
                StepId = stepId,
                ExecutionId = executionIds[3],
                CallId = $"workflow:{runId}:{stepId}:{executionIds[3]}",
                Attempt = 1,
                ContinuationId = "continuation-4",
                Success = new WorkflowToolCallAttemptSuccessOutcome { ResultJson = "{}" },
            }, executionIds[3]),
            (new WorkflowToolCallTimeoutFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                ExecutionId = executionIds[4],
                CallId = $"workflow:{runId}:{stepId}:{executionIds[4]}",
                TimeoutMs = 300_000,
                ContinuationId = "continuation-5",
            }, executionIds[4]),
        ];
    }

    private static string BuildProductionScaleRunId()
    {
        const string scopeId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        return $"scope-workflow:{scopeId}-237314c299:" +
               "workflow-external-document-processing-3054bff174:gagent-service:deployment:" +
               $"{scopeId}:default:default:workflow-external-document-processing:" +
               $"rev-20260815001423-9354:run:{new string('c', 32)}";
    }
}
