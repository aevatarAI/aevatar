using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowFileArtifactCleanupHostedService : IHostedService, IDisposable
{
    private readonly IFileArtifactCleanupPort _cleanupPort;
    private readonly IOptions<WorkflowFileArtifactOptions> _options;
    private readonly ILogger<WorkflowFileArtifactCleanupHostedService> _logger;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    public WorkflowFileArtifactCleanupHostedService(
        IFileArtifactCleanupPort cleanupPort,
        IOptions<WorkflowFileArtifactOptions> options,
        ILogger<WorkflowFileArtifactCleanupHostedService> logger)
    {
        _cleanupPort = cleanupPort;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.CleanupEnabled)
            return;

        if (options.CleanupOnStart)
            await CleanupOnceAsync(cancellationToken).ConfigureAwait(false);

        if (options.CleanupInterval <= TimeSpan.Zero)
            return;

        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunPeriodicCleanupAsync(options.CleanupInterval, _stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loop == null || _stopping == null)
            return;

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stopping?.Dispose();
    }

    private async Task RunPeriodicCleanupAsync(TimeSpan cleanupInterval, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(cleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await CleanupOnceAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task CleanupOnceAsync(CancellationToken cancellationToken)
    {
        var request = new FileArtifactCleanupRequest(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var result = await _cleanupPort.CleanupAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.DeletedArtifactCount > 0)
        {
            _logger.LogInformation(
                "Workflow file artifact cleanup deleted {DeletedArtifactCount} artifacts from {ScannedArtifactCount} scanned artifacts.",
                result.DeletedArtifactCount,
                result.ScannedArtifactCount);
        }
    }
}
