using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

internal static class WorkflowToolCallTelemetry
{
    internal const string MeterName = "Aevatar.Workflow";
    internal const string WaterlineTotalMetricName = "aevatar.workflow.tool_call.waterline.total";
    internal const string PhaseDurationMetricName = "aevatar.workflow.tool_call.phase.duration";
    internal const string WaterlineTag = "waterline";
    internal const string PhaseTag = "phase";
    internal const string DispositionTag = "disposition";
    internal const string DeliveryMethodTag = "delivery.method";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> WaterlineTotal = Meter.CreateCounter<long>(
        WaterlineTotalMetricName,
        description: "Correlated workflow tool-call timing waterlines.");
    private static readonly Histogram<double> PhaseDuration = Meter.CreateHistogram<double>(
        PhaseDurationMetricName,
        unit: "ms",
        description: "Elapsed time for bounded workflow tool-call phases.");

    internal static void Record(
        ILogger logger,
        WorkflowToolCallAttemptTimingObservation observation)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(observation);

        SafeObserve(() =>
        {
            var tags = Tags(observation);
            WaterlineTotal.Add(1, tags);
            if (TryReadElapsed(observation, out var elapsedMs))
                PhaseDuration.Record(Math.Max(0, elapsedMs), tags);
        });
        SafeObserve(() => Log(logger, observation));
    }

    private static void Log(
        ILogger logger,
        WorkflowToolCallAttemptTimingObservation observation)
    {
        logger.LogInformation(
            "Workflow tool-call timing waterline observed. scopeId={ScopeId} runId={RunId} stepId={StepId} callId={CallId} executionId={ExecutionId} continuationId={ContinuationId} attempt={Attempt} dispatchId={DispatchId} waterline={Waterline} observedAtUtc={ObservedAtUtc} transportAttempt={TransportAttempt} providerDisposition={ProviderDisposition} deliveryMethod={DeliveryMethod} deliveryAcceptance={DeliveryAcceptance} reconciliationDisposition={ReconciliationDisposition} elapsedPhase={ElapsedPhase} elapsedMs={ElapsedMs} committedEventId={CommittedEventId} committedStateVersion={CommittedStateVersion}",
            observation.ScopeId,
            observation.RunId,
            observation.StepId,
            observation.CallId,
            observation.ExecutionId,
            observation.ContinuationId,
            observation.Attempt,
            observation.DispatchId,
            WaterlineValue(observation.Waterline),
            observation.ObservedAtUtc?.ToDateTimeOffset(),
            observation.TransportAttempt,
            ProviderDispositionValue(observation.ProviderDisposition),
            DeliveryMethodValue(observation.DeliveryMethod),
            DeliveryAcceptanceValue(observation.DeliveryAcceptance),
            ReconciliationDispositionValue(observation.ReconciliationDisposition),
            PhaseValue(observation),
            TryReadElapsed(observation, out var elapsedMs) ? elapsedMs : null,
            observation.CommittedEventId,
            observation.CommittedStateVersion);
    }

    private static TagList Tags(WorkflowToolCallAttemptTimingObservation observation) =>
        new()
        {
            { WaterlineTag, WaterlineValue(observation.Waterline) },
            { PhaseTag, PhaseValue(observation) },
            { DispositionTag, DispositionValue(observation) },
            { DeliveryMethodTag, DeliveryMethodValue(observation.DeliveryMethod) },
        };

    private static string WaterlineValue(WorkflowToolCallAttemptWaterline waterline) =>
        waterline switch
        {
            WorkflowToolCallAttemptWaterline.PendingStatePersisted => "pending_state_persisted",
            WorkflowToolCallAttemptWaterline.ExternalDispatchStarted => "external_dispatch_started",
            WorkflowToolCallAttemptWaterline.ProviderReturned => "provider_returned",
            WorkflowToolCallAttemptWaterline.CompletionDeliveryProducerConfirmed =>
                "completion_delivery_producer_confirmed",
            WorkflowToolCallAttemptWaterline.TimeoutSignalAccepted => "timeout_signal_accepted",
            WorkflowToolCallAttemptWaterline.ActorReconciliationCompleted =>
                "actor_reconciliation_completed",
            _ => "unspecified",
        };

    private static string PhaseValue(WorkflowToolCallAttemptTimingObservation observation) =>
        observation.ElapsedCase switch
        {
            WorkflowToolCallAttemptTimingObservation.ElapsedOneofCase.PreparationElapsedMs => "actor_preparation",
            WorkflowToolCallAttemptTimingObservation.ElapsedOneofCase.ExternalExecutionElapsedMs =>
                "external_execution",
            WorkflowToolCallAttemptTimingObservation.ElapsedOneofCase.CompletionDeliveryElapsedMs =>
                "completion_delivery",
            WorkflowToolCallAttemptTimingObservation.ElapsedOneofCase.ActorReconciliationElapsedMs =>
                "actor_reconciliation",
            _ => observation.Waterline == WorkflowToolCallAttemptWaterline.TimeoutSignalAccepted
                ? "timeout_acceptance"
                : "none",
        };

    private static string DispositionValue(WorkflowToolCallAttemptTimingObservation observation)
    {
        if (observation.ReconciliationDisposition != WorkflowToolCallReconciliationDisposition.Unspecified)
            return ReconciliationDispositionValue(observation.ReconciliationDisposition);
        if (observation.DeliveryAcceptance != WorkflowToolCallCompletionDeliveryAcceptance.Unspecified)
            return DeliveryAcceptanceValue(observation.DeliveryAcceptance);
        if (observation.ProviderDisposition != WorkflowToolCallProviderDisposition.Unspecified)
            return ProviderDispositionValue(observation.ProviderDisposition);
        return "none";
    }

    private static string ProviderDispositionValue(WorkflowToolCallProviderDisposition disposition) =>
        disposition switch
        {
            WorkflowToolCallProviderDisposition.Succeeded => "succeeded",
            WorkflowToolCallProviderDisposition.Failed => "failed",
            WorkflowToolCallProviderDisposition.ApprovalRequired => "approval_required",
            WorkflowToolCallProviderDisposition.PendingOperation => "pending_operation",
            WorkflowToolCallProviderDisposition.Threw => "threw",
            WorkflowToolCallProviderDisposition.Cancelled => "cancelled",
            _ => "none",
        };

    private static string DeliveryMethodValue(WorkflowToolCallCompletionDeliveryMethod method) =>
        method switch
        {
            WorkflowToolCallCompletionDeliveryMethod.SelfPublish => "self_publish",
            WorkflowToolCallCompletionDeliveryMethod.DurableCallback => "durable_callback",
            _ => "none",
        };

    private static string DeliveryAcceptanceValue(WorkflowToolCallCompletionDeliveryAcceptance acceptance) =>
        acceptance switch
        {
            WorkflowToolCallCompletionDeliveryAcceptance.Confirmed => "confirmed",
            WorkflowToolCallCompletionDeliveryAcceptance.Unknown => "unknown",
            _ => "none",
        };

    private static string ReconciliationDispositionValue(WorkflowToolCallReconciliationDisposition disposition) =>
        disposition switch
        {
            WorkflowToolCallReconciliationDisposition.Succeeded => "succeeded",
            WorkflowToolCallReconciliationDisposition.ProviderFailed => "provider_failed",
            WorkflowToolCallReconciliationDisposition.ApprovalSuspended => "approval_suspended",
            WorkflowToolCallReconciliationDisposition.PendingOperation => "pending_operation",
            WorkflowToolCallReconciliationDisposition.RetryScheduled => "retry_scheduled",
            WorkflowToolCallReconciliationDisposition.TimeoutOutcomeUnknown => "timeout_outcome_unknown",
            WorkflowToolCallReconciliationDisposition.RetryDeadlineExceeded => "retry_deadline_exceeded",
            WorkflowToolCallReconciliationDisposition.Duplicate => "duplicate",
            WorkflowToolCallReconciliationDisposition.Stale => "stale",
            WorkflowToolCallReconciliationDisposition.Untrusted => "untrusted",
            WorkflowToolCallReconciliationDisposition.NoPendingExecution => "no_pending_execution",
            WorkflowToolCallReconciliationDisposition.ProtectedMaterialUnavailable =>
                "protected_material_unavailable",
            WorkflowToolCallReconciliationDisposition.ApprovalDeadlineExceeded =>
                "approval_deadline_exceeded",
            WorkflowToolCallReconciliationDisposition.PreDispatchFailed => "pre_dispatch_failed",
            WorkflowToolCallReconciliationDisposition.RecoveryOutcomeUnknown => "recovery_outcome_unknown",
            _ => "none",
        };

    private static bool TryReadElapsed(
        WorkflowToolCallAttemptTimingObservation observation,
        out double elapsedMs)
    {
        switch (observation.ElapsedCase)
        {
            case WorkflowToolCallAttemptTimingObservation.ElapsedOneofCase.PreparationElapsedMs:
                elapsedMs = observation.PreparationElapsedMs;
                return true;
            case WorkflowToolCallAttemptTimingObservation.ElapsedOneofCase.ExternalExecutionElapsedMs:
                elapsedMs = observation.ExternalExecutionElapsedMs;
                return true;
            case WorkflowToolCallAttemptTimingObservation.ElapsedOneofCase.CompletionDeliveryElapsedMs:
                elapsedMs = observation.CompletionDeliveryElapsedMs;
                return true;
            case WorkflowToolCallAttemptTimingObservation.ElapsedOneofCase.ActorReconciliationElapsedMs:
                elapsedMs = observation.ActorReconciliationElapsedMs;
                return true;
            default:
                elapsedMs = 0;
                return false;
        }
    }

    private static void SafeObserve(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            try
            {
                Trace.TraceWarning(
                    "Workflow tool-call telemetry emission failed. errorType={0}",
                    exception.GetType().Name);
            }
            catch (Exception)
            {
                return;
            }
        }
    }
}
