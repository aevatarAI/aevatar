using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static partial class WorkflowCapabilityEndpoints
{
    internal static async Task<IResult> HandleResume(
        WorkflowResumeInput input,
        IWorkflowRunActorPort actorPort,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actorPort);

        var runActorId = (input.RunActorId ?? string.Empty).Trim();
        var runId = (input.RunId ?? string.Empty).Trim();
        var resumeToken = (input.ResumeToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(runActorId) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(resumeToken))
        {
            return Results.BadRequest(new { error = "runActorId, runId and resumeToken are required." });
        }

        var actor = await actorPort.GetRunActorAsync(runActorId, ct);
        if (actor == null)
            return Results.NotFound(new { error = $"Run actor '{runActorId}' not found." });

        if (!await actorPort.IsWorkflowRunActorAsync(actor, ct))
            return Results.BadRequest(new { error = $"Actor '{runActorId}' is not a workflow run actor." });

        var resumed = new WorkflowResumedEvent
        {
            RunId = runId,
            Approved = input.Approved,
            UserInput = input.UserInput ?? string.Empty,
            ResumeToken = resumeToken,
        };
        if (input.Metadata is { Count: > 0 })
        {
            foreach (var (key, value) in input.Metadata)
                resumed.Metadata[key] = value;
        }
        var commandId = (input.CommandId ?? string.Empty).Trim();
        var correlationId = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(resumed),
            PublisherId = "api.workflow.resume",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            TargetActorId = actor.Id,
        }, ct);

        return Results.Ok(new
        {
            accepted = true,
            runActorId,
            runId,
            resumeToken,
            commandId = correlationId,
        });
    }

    internal static async Task<IResult> HandleSignal(
        WorkflowSignalInput input,
        IWorkflowRunActorPort actorPort,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actorPort);

        var runActorId = (input.RunActorId ?? string.Empty).Trim();
        var runId = (input.RunId ?? string.Empty).Trim();
        var waitToken = (input.WaitToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(runActorId) ||
            string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(waitToken))
        {
            return Results.BadRequest(new { error = "runActorId, runId and waitToken are required." });
        }

        var actor = await actorPort.GetRunActorAsync(runActorId, ct);
        if (actor == null)
            return Results.NotFound(new { error = $"Run actor '{runActorId}' not found." });

        if (!await actorPort.IsWorkflowRunActorAsync(actor, ct))
            return Results.BadRequest(new { error = $"Actor '{runActorId}' is not a workflow run actor." });

        var commandId = (input.CommandId ?? string.Empty).Trim();
        var correlationId = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new SignalReceivedEvent
            {
                RunId = runId,
                Payload = input.Payload ?? string.Empty,
                WaitToken = waitToken,
            }),
            PublisherId = "api.workflow.signal",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            TargetActorId = actor.Id,
        }, ct);

        return Results.Ok(new
        {
            accepted = true,
            runActorId,
            runId,
            waitToken,
            commandId = correlationId,
        });
    }
}
