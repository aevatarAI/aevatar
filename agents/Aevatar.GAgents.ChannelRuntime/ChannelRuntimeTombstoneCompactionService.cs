using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelRuntimeTombstoneCompactionService : BackgroundService
{
    private readonly ChannelRuntimeTombstoneCompactor _compactor;
    private readonly ChannelRuntimeTombstoneCompactionOptions _options;
    private readonly ILogger<ChannelRuntimeTombstoneCompactionService> _logger;

    public ChannelRuntimeTombstoneCompactionService(
        ChannelRuntimeTombstoneCompactor compactor,
        IOptions<ChannelRuntimeTombstoneCompactionOptions> options,
        ILogger<ChannelRuntimeTombstoneCompactionService> logger)
    {
        _compactor = compactor ?? throw new ArgumentNullException(nameof(compactor));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = _options.PollInterval > TimeSpan.Zero
            ? _options.PollInterval
            : TimeSpan.FromMinutes(1);

        using var timer = new PeriodicTimer(pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _compactor.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Channel runtime tombstone compaction pass failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
