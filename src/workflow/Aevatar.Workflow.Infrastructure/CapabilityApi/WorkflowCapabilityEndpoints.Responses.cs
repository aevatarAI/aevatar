using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static partial class WorkflowCapabilityEndpoints
{
    private static WorkflowOutputFrame BuildRunContextFrame(WorkflowChatRunStarted started) =>
        new()
        {
            Type = WorkflowRunEventTypes.Custom,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Name = "aevatar.run.context",
            Value = new
            {
                started.RunActorId,
                started.DefinitionActorId,
                started.WorkflowName,
                started.CommandId,
            },
        };

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsJsonAsync(
            new
            {
                code,
                message,
            },
            cancellationToken: ct);
    }

    private static async Task WriteStreamErrorFrameAsync(
        ChatSseResponseWriter writer,
        Exception ex,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(
                new WorkflowOutputFrame
                {
                    Type = WorkflowRunEventTypes.RunError,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Code = "EXECUTION_FAILED",
                    Message = $"Workflow execution failed: {SanitizeErrorMessage(ex.Message)}",
                },
                ct);
        }
        catch (Exception writeEx)
        {
            logger?.LogDebug(writeEx, "Failed to write SSE error frame because the stream is no longer writable.");
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
