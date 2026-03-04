using System.Diagnostics;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class CapabilityTraceContext
{
    private static readonly ActivitySource WorkflowRunSource = new("Aevatar.Workflow.Run", "1.0.0");

    public static string CurrentTraceId() =>
        Activity.Current?.TraceId.ToString() ?? string.Empty;

    public static void ApplyTraceHeaders(HttpResponse response)
    {
        var activity = Activity.Current;
        if (activity == null)
            return;

        var traceId = activity.TraceId.ToString();
        if (!string.IsNullOrWhiteSpace(traceId))
            response.Headers["X-Trace-Id"] = traceId;
    }

    public static void ApplyCorrelationHeader(HttpResponse response, string correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
            response.Headers["X-Correlation-Id"] = correlationId;
    }

    public static WorkflowRunAcceptedPayload CreateAcceptedPayload(WorkflowChatRunStarted started) =>
        new()
        {
            CommandId = started.CommandId,
            CorrelationId = started.CommandId,
            TraceId = CurrentTraceId(),
            ActorId = started.ActorId,
        };

    public static Dictionary<string, object?> CreateApiLogScopeState(
        string correlationId = "",
        string causationId = "") =>
        new()
        {
            ["trace_id"] = CurrentTraceId(),
            ["correlation_id"] = correlationId,
            ["causation_id"] = causationId,
        };

    public static IDisposable? BeginApiScope(
        ILogger? logger,
        string correlationId = "",
        string causationId = "") =>
        logger?.BeginScope(CreateApiLogScopeState(correlationId, causationId));

    public static Activity? StartWorkflowRunExecute(
        string? workflowName,
        string? agentId,
        string channel)
    {
        var activity = WorkflowRunSource.StartActivity("workflow.run.execute", ActivityKind.Internal);
        if (activity == null)
            return null;

        activity.SetTag("aevatar.run.channel", channel);
        if (!string.IsNullOrWhiteSpace(workflowName))
            activity.SetTag("aevatar.workflow.name", workflowName);
        if (!string.IsNullOrWhiteSpace(agentId))
            activity.SetTag("aevatar.workflow.agent_id", agentId);
        return activity;
    }

    public static void MarkRunStarted(Activity? activity, WorkflowChatRunStarted started)
    {
        if (activity == null)
            return;

        activity.SetTag("aevatar.workflow.command_id", started.CommandId);
        activity.SetTag("aevatar.workflow.actor_id", started.ActorId);
    }

    public static void MarkFirstOutputLatency(Activity? activity, long elapsedMilliseconds)
    {
        if (activity == null)
            return;

        if (activity.GetTagItem("aevatar.workflow.first_output_latency_ms") != null)
            return;

        activity.SetTag("aevatar.workflow.first_output_latency_ms", elapsedMilliseconds);
    }

    public static void MarkRunFinished(
        Activity? activity,
        WorkflowChatRunStartError error,
        long elapsedMilliseconds)
    {
        if (activity == null)
            return;

        activity.SetTag("aevatar.workflow.duration_ms", elapsedMilliseconds);
        activity.SetTag("aevatar.workflow.start_error", error.ToString());
        if (error == WorkflowChatRunStartError.None)
            return;

        activity.SetStatus(ActivityStatusCode.Error, error.ToString());
    }
}

internal sealed record WorkflowRunAcceptedPayload
{
    public required string CommandId { get; init; }
    public required string CorrelationId { get; init; }
    public required string TraceId { get; init; }
    public required string ActorId { get; init; }
}
