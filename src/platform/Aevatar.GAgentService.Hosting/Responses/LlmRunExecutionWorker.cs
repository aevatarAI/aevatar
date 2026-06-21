using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Hosting.Responses;

// Drains the off-grain LLM run execution queue and runs each request OFF any Orleans grain
// turn. This is the fix for epic #2271: the run loop must never occupy an actor/grain
// event-handler turn (doing so trips Orleans' 30s stream-delivery timeout and self-deadlocks
// against the very deliveries the loop awaits). Here the loop runs on a normal task; its
// blocking waits (sink.ReadAllAsync) only occupy a thread-pool thread, so the session actor
// and projection pulling agents stay free to interleave the short Record* turns.
public sealed class LlmRunExecutionWorker : BackgroundService
{
    private readonly ILlmRunExecutionQueue _queue;
    private readonly ILlmRunExecutionService _executionService;
    private readonly LlmRunExecutionWorkerOptions _options;
    private readonly ILogger<LlmRunExecutionWorker> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly int _maxConcurrency;

    public LlmRunExecutionWorker(
        ILlmRunExecutionQueue queue,
        ILlmRunExecutionService executionService,
        IOptions<LlmRunExecutionWorkerOptions> options,
        ILogger<LlmRunExecutionWorker> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxConcurrency = _options.MaxConcurrency > 0 ? _options.MaxConcurrency : 1;
        _concurrency = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // Bound per-host concurrency; admission stops accepting new runs on shutdown.
                await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
                _ = RunOneAsync(request).ContinueWith(
                    static (_, state) => ((SemaphoreSlim)state!).Release(),
                    _concurrency,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown; fall through to drain in-flight runs.
        }

        await DrainInFlightAsync().ConfigureAwait(false);
    }

    // Runs the full LLM loop. The run is deliberately NOT bound to the host stopping token:
    // cancelling it mid-flight would risk dropping the terminal event. The executor always
    // dispatches a terminal (completed/failed/cancelled) even on its own exceptions; a
    // process death before that is finalized by the session actor's durable run-timeout.
    private async Task RunOneAsync(LlmRunExecutionRequest request)
    {
        try
        {
            await _executionService.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last-resort guard so one faulting run never tears down the worker loop.
            _logger.LogError(
                ex,
                "Off-grain LLM run executor faulted for response {ResponseId} run {RunId}.",
                request.ResponseId,
                request.RunId);
        }
    }

    // Acquiring every concurrency permit means all in-flight runs have released theirs, i.e.
    // finished. Bounded by the drain grace; runs still running after the grace are left to the
    // session actor's run-timeout finalizer rather than cancelled (no swallowed terminal).
    private async Task DrainInFlightAsync()
    {
        using var graceCts = new CancellationTokenSource(_options.ShutdownDrainGrace);
        var acquired = 0;
        try
        {
            for (; acquired < _maxConcurrency; acquired++)
                await _concurrency.WaitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "LLM run execution worker shutdown grace ({GraceSeconds}s) elapsed with {Running} run(s) still in flight; they rely on the session actor run-timeout finalizer.",
                _options.ShutdownDrainGraceSeconds,
                _maxConcurrency - acquired);
        }
        finally
        {
            if (acquired > 0)
                _concurrency.Release(acquired);
        }
    }

    public override void Dispose()
    {
        _concurrency.Dispose();
        base.Dispose();
    }
}
