using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Maker.Infrastructure.Runs;

public sealed class WorkflowMakerRunExecutionPort : IMakerRunExecutionPort
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streamProvider;
    private readonly ILogger<WorkflowMakerRunExecutionPort> _logger;

    public WorkflowMakerRunExecutionPort(
        IActorRuntime runtime,
        IStreamProvider streamProvider,
        ILogger<WorkflowMakerRunExecutionPort> logger)
    {
        _runtime = runtime;
        _streamProvider = streamProvider;
        _logger = logger;
    }

    public async Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveOrCreateAsync(request.ActorId, ct);
        var actor = resolved.Actor;
        var actorCreated = resolved.Created;

        ConfigureWorkflow(actor, request);

        var correlationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var started = new MakerRunStarted(actor.Id, request.WorkflowName, correlationId, startedAt);
        var stream = _streamProvider.GetStream(actor.Id);
        var completedTcs = new TaskCompletionSource<MakerRunCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (TryResolveCompletion(envelope, out var completion))
                completedTcs.TrySetResult(completion);

            return Task.CompletedTask;
        }, ct);

        await actor.HandleEventAsync(CreateStartEnvelope(request, correlationId), ct);

        var timeout = request.Timeout ?? TimeSpan.FromMinutes(10);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        MakerRunCompletion? completed = null;
        var timedOut = false;

        try
        {
            completed = await completedTcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            _logger.LogWarning(
                "Maker run timed out. actor={ActorId}, workflow={WorkflowName}",
                actor.Id,
                request.WorkflowName);
        }

        if (request.DestroyActorAfterRun || actorCreated)
        {
            try
            {
                await _runtime.DestroyAsync(actor.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Destroy actor failed after maker run. actor={ActorId}", actor.Id);
            }
        }

        return new MakerRunExecutionResult(
            started,
            Output: completed?.Output ?? string.Empty,
            Success: completed?.Success == true,
            TimedOut: timedOut,
            Error: timedOut ? "Timed out" : completed?.Error);
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

    private static bool TryResolveCompletion(EventEnvelope envelope, out MakerRunCompletion completion)
    {
        completion = default!;
        var payload = envelope.Payload;
        if (payload is null || !payload.Is(WorkflowCompletedEvent.Descriptor))
            return false;

        var evt = payload.Unpack<WorkflowCompletedEvent>();
        completion = new MakerRunCompletion(
            Output: evt.Output ?? string.Empty,
            Success: evt.Success,
            Error: evt.Error);
        return true;
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
