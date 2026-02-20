using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Aevatar.Maker.Infrastructure.Runs;

public sealed class WorkflowMakerRunExecutionPort : IMakerRunExecutionPort
{
    private readonly IActorRuntime _runtime;
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly ILogger<WorkflowMakerRunExecutionPort> _logger;

    public WorkflowMakerRunExecutionPort(
        IActorRuntime runtime,
        IWorkflowExecutionProjectionPort projectionPort,
        ILogger<WorkflowMakerRunExecutionPort> logger)
    {
        _runtime = runtime;
        _projectionPort = projectionPort;
        _logger = logger;
    }

    public async Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveOrCreateAsync(request.ActorId, ct);
        var actor = resolved.Actor;
        var actorCreated = resolved.Created;

        ConfigureWorkflow(actor, request);

        var commandId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var started = new MakerRunStarted(actor.Id, request.WorkflowName, commandId, startedAt);
        var shouldDestroyActor = request.DestroyActorAfterRun || actorCreated;
        IWorkflowExecutionProjectionLease? projectionLease = null;
        MakerRunExecutionResult result;

        try
        {
            if (!_projectionPort.ProjectionEnabled)
            {
                return new MakerRunExecutionResult(
                    started,
                    Output: string.Empty,
                    Success: false,
                    TimedOut: false,
                    Error: "Projection pipeline is disabled.");
            }

            var sink = new WorkflowRunEventChannel();
            MakerRunCompletion? completed = null;
            var timedOut = false;

            try
            {
                projectionLease = await _projectionPort.EnsureActorProjectionAsync(
                    actor.Id,
                    request.WorkflowName,
                    request.Input,
                    commandId,
                    ct);
                if (projectionLease == null)
                {
                    return new MakerRunExecutionResult(
                        started,
                        Output: string.Empty,
                        Success: false,
                        TimedOut: false,
                        Error: "Projection pipeline is disabled.");
                }

                await _projectionPort.AttachLiveSinkAsync(projectionLease, sink, ct);

                await actor.HandleEventAsync(CreateStartEnvelope(request, commandId), ct);

                var timeout = request.Timeout ?? TimeSpan.FromMinutes(10);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                try
                {
                    completed = await WaitForCompletionAsync(sink, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    timedOut = true;
                    _logger.LogWarning(
                        "Maker run timed out. actor={ActorId}, workflow={WorkflowName}",
                        actor.Id,
                        request.WorkflowName);
                }
            }
            finally
            {
                if (projectionLease != null)
                    await _projectionPort.DetachLiveSinkAsync(projectionLease, sink, CancellationToken.None);
                sink.Complete();
                await sink.DisposeAsync();
            }

            result = new MakerRunExecutionResult(
                started,
                Output: completed?.Output ?? string.Empty,
                Success: completed?.Success == true,
                TimedOut: timedOut,
                Error: timedOut
                    ? "Timed out"
                    : completed?.Error ?? (completed == null ? "No completion event from projection." : null));
        }
        finally
        {
            if (shouldDestroyActor)
            {
                try
                {
                    if (projectionLease != null)
                        await _projectionPort.ReleaseActorProjectionAsync(projectionLease, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Release projection failed after maker run. actor={ActorId}", actor.Id);
                }

                try
                {
                    await _runtime.DestroyAsync(actor.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Destroy actor failed after maker run. actor={ActorId}", actor.Id);
                }
            }
        }

        return result;
    }

    private async Task<MakerResolvedActor> ResolveOrCreateAsync(string? actorId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(actorId))
        {
            var actor = await _runtime.GetAsync(actorId)
                ?? throw new InvalidOperationException($"Actor '{actorId}' not found.");
            EnsureWorkflowAgent(actor);
            return new MakerResolvedActor(actor, Created: false);
        }

        var created = await _runtime.CreateAsync<WorkflowGAgent>(ct: ct);
        EnsureWorkflowAgent(created);
        return new MakerResolvedActor(created, Created: true);
    }

    private static void ConfigureWorkflow(IActor actor, MakerRunRequest request)
    {
        var workflow = EnsureWorkflowAgent(actor);
        workflow.ConfigureWorkflow(request.WorkflowYaml, request.WorkflowName);
    }

    private static EventEnvelope CreateStartEnvelope(MakerRunRequest request, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = request.Input,
                SessionId = correlationId,
            }),
            PublisherId = "maker.application",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
        };

    private static async Task<MakerRunCompletion?> WaitForCompletionAsync(
        IWorkflowRunEventSink sink,
        CancellationToken ct)
    {
        await foreach (var evt in sink.ReadAllAsync(ct))
        {
            if (evt is WorkflowRunFinishedEvent finished)
            {
                return new MakerRunCompletion(
                    Output: ResolveOutput(finished.Result),
                    Success: true,
                    Error: null);
            }

            if (evt is WorkflowRunErrorEvent error)
            {
                return new MakerRunCompletion(
                    Output: string.Empty,
                    Success: false,
                    Error: error.Message);
            }
        }

        return null;
    }

    private static string ResolveOutput(object? result)
    {
        if (result == null)
            return string.Empty;

        if (result is string outputText)
            return outputText;

        if (result is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.String)
                return json.GetString() ?? string.Empty;
            if (json.ValueKind == JsonValueKind.Object &&
                json.TryGetProperty("output", out var outputProp))
                return outputProp.ValueKind == JsonValueKind.String
                    ? outputProp.GetString() ?? string.Empty
                    : outputProp.ToString();
            return json.ToString();
        }

        var outputProperty = result.GetType().GetProperty("output")
                            ?? result.GetType().GetProperty("Output");
        if (outputProperty?.GetValue(result) is { } value)
            return value.ToString() ?? string.Empty;

        return result.ToString() ?? string.Empty;
    }

    private static WorkflowGAgent EnsureWorkflowAgent(IActor actor)
    {
        if (actor.Agent is WorkflowGAgent workflow)
            return workflow;

        throw new InvalidOperationException("Current actor adapter requires WorkflowGAgent.");
    }

    private sealed record MakerResolvedActor(IActor Actor, bool Created);

    private sealed record MakerRunCompletion(string Output, bool Success, string? Error);
}
