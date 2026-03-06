using System.Diagnostics;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class CapabilityTraceContext
{
    public static string CurrentTraceId() =>
        Activity.Current?.TraceId.ToString() ?? string.Empty;

    public static CapabilityMessageTraceContext CreateMessageContext(
        string correlationId = "",
        string fallbackCorrelationId = "") =>
        new()
        {
            TraceId = CurrentTraceId(),
            CorrelationId = ResolveCorrelationId(correlationId, fallbackCorrelationId),
        };

    public static string ResolveCorrelationId(
        string correlationId = "",
        string fallbackCorrelationId = "") =>
        !string.IsNullOrWhiteSpace(correlationId)
            ? correlationId
            : (fallbackCorrelationId ?? string.Empty);

    public static void ApplyCorrelationHeader(HttpResponse response, string correlationId)
    {
        var context = CreateMessageContext(correlationId);
        if (!string.IsNullOrWhiteSpace(context.CorrelationId))
            response.Headers["X-Correlation-Id"] = context.CorrelationId;
    }

    public static WorkflowRunAcceptedPayload CreateAcceptedPayload(WorkflowChatRunStarted started) =>
        CreateAcceptedPayload(started.CommandId, started.RunActorId, started.DefinitionActorId);

    public static WorkflowRunAcceptedPayload CreateAcceptedPayload(
        string commandId,
        string runActorId,
        string? definitionActorId)
    {
        var context = CreateMessageContext(commandId);
        return new WorkflowRunAcceptedPayload
        {
            CommandId = commandId,
            CorrelationId = context.CorrelationId,
            RunActorId = runActorId,
            DefinitionActorId = definitionActorId,
        };
    }

}

internal sealed record CapabilityMessageTraceContext
{
    public required string CorrelationId { get; init; }
    public required string TraceId { get; init; }
}

internal sealed record WorkflowRunAcceptedPayload
{
    public required string CommandId { get; init; }
    public required string CorrelationId { get; init; }
    public required string RunActorId { get; init; }
    public string? DefinitionActorId { get; init; }
}
