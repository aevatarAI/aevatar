using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeFleetAuthoritySiloLifecycleParticipant
    : ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable, IDisposable
{
    private readonly IActorRuntime _runtime;
    private readonly IClusterMembershipService? _membership;
    private readonly IHostApplicationLifetime? _applicationLifetime;
    private readonly ILogger<RuntimeFleetAuthoritySiloLifecycleParticipant> _logger;
    private readonly RuntimeFleetAuthorityBootstrapOptions _options;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _provisionLoop;

    public RuntimeFleetAuthoritySiloLifecycleParticipant(
        IActorRuntime runtime,
        IClusterMembershipService? membership = null,
        IHostApplicationLifetime? applicationLifetime = null,
        ILogger<RuntimeFleetAuthoritySiloLifecycleParticipant>? logger = null,
        RuntimeFleetAuthorityBootstrapOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _membership = membership;
        _applicationLifetime = applicationLifetime;
        _logger = logger ?? NullLogger<RuntimeFleetAuthoritySiloLifecycleParticipant>.Instance;
        _options = options ?? new RuntimeFleetAuthorityBootstrapOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.InitialRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            _options.MaxRetryDelay,
            _options.InitialRetryDelay);
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe(
            nameof(RuntimeFleetAuthoritySiloLifecycleParticipant),
            ServiceLifecycleStage.Active,
            StartAsync);
    }

    internal Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_provisionLoop is { IsCompleted: false })
                return Task.CompletedTask;

            _stop?.Dispose();
            _stop = _applicationLifetime == null
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(
                    _applicationLifetime.ApplicationStopping);
            _provisionLoop = Task.Run(
                () => RunProvisionLoopAsync(_stop.Token),
                CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    internal Task ProvisionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _runtime.CreateByKindAsync(
            RuntimeFleetCapabilityAuthorityIdentity.AgentKind,
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            ct);
    }

    private async Task RunProvisionLoopAsync(CancellationToken ct)
    {
        var retryDelay = _options.InitialRetryDelay;
        var observedMembershipVersion = _membership?.CurrentSnapshot.Version;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProvisionAsync(ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Fleet capability authority provisioning failed; the runtime will retry without blocking silo activation.");
            }

            var currentMembershipVersion = _membership?.CurrentSnapshot.Version;
            if (currentMembershipVersion != null &&
                !Equals(currentMembershipVersion, observedMembershipVersion))
            {
                observedMembershipVersion = currentMembershipVersion;
                retryDelay = _options.InitialRetryDelay;
            }

            try
            {
                await Task.Delay(retryDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                _options.MaxRetryDelay.TotalMilliseconds,
                retryDelay.TotalMilliseconds * 2));
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _stop?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _stop?.Cancel();
            loop = _provisionLoop;
        }

        if (loop != null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_gate)
        {
            _stop?.Dispose();
            _stop = null;
            _provisionLoop = null;
        }

        GC.SuppressFinalize(this);
    }
}

internal sealed record RuntimeFleetAuthorityBootstrapOptions
{
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}
