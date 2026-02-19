using Aevatar.Foundation.Abstractions;
using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Maker.Projection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Maker.Application.Runs;

public sealed class MakerRunApplicationService : IMakerRunApplicationService
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streamProvider;
    private readonly IMakerRunActorAdapter _actorAdapter;
    private readonly ILogger<MakerRunApplicationService> _logger;

    public MakerRunApplicationService(
        IActorRuntime runtime,
        IStreamProvider streamProvider,
        IMakerRunActorAdapter actorAdapter,
        ILogger<MakerRunApplicationService> logger)
    {
        _runtime = runtime;
        _streamProvider = streamProvider;
        _actorAdapter = actorAdapter;
        _logger = logger;
    }

    public async Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await _actorAdapter.ResolveOrCreateAsync(_runtime, request.ActorId, ct);
        var actor = resolved.Actor;
        var actorCreated = resolved.Created;
        await _actorAdapter.ConfigureAsync(actor, request, ct);

        var correlationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var started = new MakerRunStarted(actor.Id, request.WorkflowName, correlationId, startedAt);
        var projection = new MakerRunProjectionAccumulator(actor.Id);
        var stream = _streamProvider.GetStream(actor.Id);
        var completedTcs = new TaskCompletionSource<MakerRunCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await stream.SubscribeAsync<EventEnvelope>(envelope =>
        {
            projection.RecordEnvelope(envelope);
            if (_actorAdapter.TryResolveCompletion(envelope, out var completion))
                completedTcs.TrySetResult(completion);

            return Task.CompletedTask;
        }, ct);

        await actor.HandleEventAsync(_actorAdapter.CreateStartEnvelope(request, correlationId), ct);

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
            Output: completed?.Output ?? string.Empty,
            Success: completed?.Success == true,
            TimedOut: timedOut,
            Error: timedOut ? "Timed out" : completed?.Error);
    }
}
