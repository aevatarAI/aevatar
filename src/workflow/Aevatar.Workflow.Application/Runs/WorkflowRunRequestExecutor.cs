using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunRequestExecutor : IWorkflowRunRequestExecutor
{
    private readonly ILogger<WorkflowRunRequestExecutor> _logger;

    public WorkflowRunRequestExecutor(ILogger<WorkflowRunRequestExecutor> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(
        IActor actor,
        string actorId,
        EventEnvelope requestEnvelope,
        IEventSink<WorkflowRunEvent> sink,
        CancellationToken ct = default)
    {
        try
        {
            await actor.HandleEventAsync(requestEnvelope, ct);
        }
        catch (Exception ex)
        {
            var payloadType = requestEnvelope.Payload?.TypeUrl ?? "(none)";
            _logger.LogError(
                ex,
                "Workflow execution failed. actorId={ActorId}, envelopeId={EnvelopeId}, correlationId={CorrelationId}, payloadType={PayloadType}",
                actorId,
                requestEnvelope.Id,
                requestEnvelope.CorrelationId,
                payloadType);
            try
            {
                await sink.PushAsync(new WorkflowRunErrorEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Message = $"Workflow execution error: {SanitizeErrorMessage(ex.Message)}",
                    Code = "INTERNAL_ERROR",
                }, ct);
            }
            catch (InvalidOperationException)
            {
            }

            sink.Complete();
        }
    }

    private static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "unknown error";

        return message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}
