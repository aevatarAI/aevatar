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

    public static WorkflowRunAcceptedPayload CreateAcceptedPayload(WorkflowChatRunAcceptedReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return CreateAcceptedPayload(receipt.CommandId, receipt.CorrelationId, receipt.ActorId);
    }

    public static WorkflowRunAcceptedPayload CreateAcceptedPayload(
        string commandId,
        string correlationId,
        string actorId)
    {
        var context = CreateMessageContext(correlationId, commandId);
        return new WorkflowRunAcceptedPayload
        {
            CommandId = commandId,
            CorrelationId = context.CorrelationId,
            ActorId = actorId,
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
    public required string ActorId { get; init; }
}
