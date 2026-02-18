using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Maker.Projection;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Maker.Application.Runs;

public sealed class MakerRunApplicationService : IMakerRunApplicationService
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streamProvider;
    private readonly ILogger<MakerRunApplicationService> _logger;

    public MakerRunApplicationService(
        IActorRuntime runtime,
        IStreamProvider streamProvider,
        ILogger<MakerRunApplicationService> logger)
    {
        _runtime = runtime;
        _streamProvider = streamProvider;
        _logger = logger;
    }

    public async Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actorCreated = false;
        IActor actor;

        if (!string.IsNullOrWhiteSpace(request.ActorId))
        {
            actor = await _runtime.GetAsync(request.ActorId)
                ?? throw new InvalidOperationException($"Actor '{request.ActorId}' not found.");
        }
        else
        {
            actor = await _runtime.CreateAsync<WorkflowGAgent>(ct: ct);
            actorCreated = true;
        }

        if (actor.Agent is not WorkflowGAgent workflow)
            throw new InvalidOperationException("Maker currently requires WorkflowGAgent actor.");

        workflow.ConfigureWorkflow(request.WorkflowYaml, request.WorkflowName);

        var startedAt = DateTimeOffset.UtcNow;
        var started = new MakerRunStarted(actor.Id, request.WorkflowName, startedAt);
        var projection = new MakerRunProjectionAccumulator(actor.Id);
        var stream = _streamProvider.GetStream(actor.Id);
        var completedTcs = new TaskCompletionSource<WorkflowCompletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            projection.RecordEnvelope(envelope);
            var payload = envelope.Payload;
            if (payload is null)
                return Task.CompletedTask;

            if (payload.Is(WorkflowCompletedEvent.Descriptor))
                completedTcs.TrySetResult(payload.Unpack<WorkflowCompletedEvent>());

            return Task.CompletedTask;
        }, ct);

        await actor.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = request.Input,
                SessionId = $"maker-{Guid.NewGuid():N}",
            }),
            PublisherId = "maker.application",
            Direction = EventDirection.Self,
        }, ct);

        var timeout = request.Timeout ?? TimeSpan.FromMinutes(10);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        WorkflowCompletedEvent? completed = null;
        var timedOut = false;

        try
        {
            completed = await completedTcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            _logger.LogWarning("Maker run timed out. actor={ActorId}, workflow={WorkflowName}", actor.Id, request.WorkflowName);
        }

        var endedAt = DateTimeOffset.UtcNow;
        var report = projection.BuildReport(
            request.WorkflowName,
            workflowPath: string.Empty,
            providerName: string.Empty,
            modelName: string.Empty,
            inputText: request.Input,
            startedAt,
            endedAt,
            timedOut,
            topology: []);

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
            report.RunId,
            Output: completed?.Output ?? string.Empty,
            Success: completed?.Success == true,
            TimedOut: timedOut,
            Error: timedOut ? "Timed out" : completed?.Error);
    }
}
