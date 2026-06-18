using System.Diagnostics.Metrics;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Observability;

internal static class WorkflowCompensationMetrics
{
    private static readonly Meter Meter = new("Aevatar.Workflow", "1.0.0");

    internal const string RequestedMetricName = "aevatar.workflow.compensation.requested_total";
    internal const string SucceededMetricName = "aevatar.workflow.compensation.succeeded_total";
    internal const string DeadLetteredMetricName = "aevatar.workflow.compensation.dead_lettered_total";
    internal const string OutcomeTag = "outcome";
    internal const string OutcomeSucceeded = "succeeded";

    public static readonly Counter<long> CompensationRequested = Meter.CreateCounter<long>(
        RequestedMetricName,
        description: "Total workflow compensation requests observed from committed saga events.");

    public static readonly Counter<long> CompensationSucceeded = Meter.CreateCounter<long>(
        SucceededMetricName,
        description: "Total successful workflow compensation steps observed from committed saga events.");

    public static readonly Counter<long> CompensationDeadLettered = Meter.CreateCounter<long>(
        DeadLetteredMetricName,
        description: "Total workflow compensation dead letters observed from committed saga events.");

    public static void ObserveCommittedPayload(Any? payload)
    {
        if (payload == null)
            return;

        if (payload.Is(CompensationRequestEvent.Descriptor))
        {
            CompensationRequested.Add(1);
            return;
        }

        if (payload.Is(CompensationStepCompletedEvent.Descriptor))
        {
            var completed = payload.Unpack<CompensationStepCompletedEvent>();
            if (completed.Success)
            {
                CompensationSucceeded.Add(1,
                [
                    new(OutcomeTag, OutcomeSucceeded),
                ]);
            }

            return;
        }

        if (payload.Is(WorkflowCompensationFailedEvent.Descriptor))
            CompensationDeadLettered.Add(1);
    }
}
