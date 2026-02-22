using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Hosting;

public sealed class ActorRestoreHostedService : IHostedService
{
    private readonly IActorRuntime _actorRuntime;
    private readonly ILogger<ActorRestoreHostedService> _logger;

    public ActorRestoreHostedService(
        IActorRuntime actorRuntime,
        ILogger<ActorRestoreHostedService> logger)
    {
        _actorRuntime = actorRuntime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _actorRuntime.RestoreAllAsync(cancellationToken);
        _logger.LogInformation("Actor runtime restore completed.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
