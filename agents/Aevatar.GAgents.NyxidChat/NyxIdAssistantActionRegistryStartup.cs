using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

internal interface INyxIdAssistantActionRegistrySource
{
    Task<string> FetchAsync(CancellationToken ct);
}

/// <summary>
/// Process-wide holder for the NyxID action registry pinned at startup. The
/// snapshot is initialized exactly once; when startup could only pin the
/// disabled fallback, recovery may upgrade it to a served registry exactly
/// once, and a served registry is never replaced afterwards.
/// </summary>
internal sealed class NyxIdAssistantActionRegistrySnapshot
{
    private NyxIdAssistantActionRegistry? _registry;

    public void Initialize(NyxIdAssistantActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (Interlocked.CompareExchange(ref _registry, registry, null) is not null)
        {
            throw new InvalidOperationException(
                "The NyxID Assistant action registry startup snapshot is already initialized.");
        }
    }

    public bool TryUpgrade(NyxIdAssistantActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.IsStartupFallback)
        {
            throw new InvalidOperationException(
                "A startup fallback registry cannot replace the current registry.");
        }

        var current = Volatile.Read(ref _registry);
        return current is { IsStartupFallback: true } &&
               ReferenceEquals(
                   Interlocked.CompareExchange(ref _registry, registry, current),
                   current);
    }

    public NyxIdAssistantActionRegistry GetRequired() =>
        Volatile.Read(ref _registry) ?? throw new InvalidOperationException(
            "The NyxID Assistant action registry startup snapshot is not initialized.");
}

internal sealed class NyxIdAssistantActionRegistryHttpSource
    : INyxIdAssistantActionRegistrySource
{
    internal const string HttpClientName = "NyxIdAssistantActionRegistry";
    internal const string RegistryPath = "/api/v1/assistant/actions";
    private const int MaximumRegistryBytes = 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NyxIdToolOptions _options;

    public NyxIdAssistantActionRegistryHttpSource(
        IHttpClientFactory httpClientFactory,
        NyxIdToolOptions options)
    {
        _httpClientFactory = httpClientFactory ??
                             throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string> FetchAsync(CancellationToken ct)
    {
        var baseUrl = _options.EffectiveApiBaseUrl?.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The NyxID API base URL is invalid for the Assistant action registry.");
        }

        var registryUri = new Uri(
            $"{baseUri.GetLeftPart(UriPartial.Authority)}{baseUri.AbsolutePath.TrimEnd('/')}{RegistryPath}",
            UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, registryUri);
        using var response = await _httpClientFactory
            .CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumRegistryBytes)
        {
            throw new InvalidOperationException(
                "The NyxID Assistant action registry exceeds the maximum size.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var registry = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            registry.Append(buffer, 0, read);
            if (registry.Length > MaximumRegistryBytes)
            {
                throw new InvalidOperationException(
                    "The NyxID Assistant action registry exceeds the maximum size.");
            }
        }

        return registry.ToString();
    }
}

internal sealed class NyxIdAssistantActionRegistryStartupService : IHostedService, IDisposable
{
    internal const int StartupFetchAttempts = 3;
    private static readonly TimeSpan DefaultStartupRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRecoveryRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryRetryDelayCeiling = TimeSpan.FromMinutes(5);

    private readonly INyxIdAssistantActionRegistrySource _source;
    private readonly NyxIdAssistantActionRegistrySnapshot _snapshot;
    private readonly ILogger<NyxIdAssistantActionRegistryStartupService> _logger;
    private readonly TimeSpan _startupRetryDelay;
    private readonly TimeSpan _recoveryRetryDelay;
    private readonly CancellationTokenSource _recoveryStopSource = new();
    private Task _recoveryTask = Task.CompletedTask;

    public NyxIdAssistantActionRegistryStartupService(
        INyxIdAssistantActionRegistrySource source,
        NyxIdAssistantActionRegistrySnapshot snapshot,
        ILogger<NyxIdAssistantActionRegistryStartupService>? logger = null)
        : this(source, snapshot, logger, DefaultStartupRetryDelay, DefaultRecoveryRetryDelay)
    {
    }

    internal NyxIdAssistantActionRegistryStartupService(
        INyxIdAssistantActionRegistrySource source,
        NyxIdAssistantActionRegistrySnapshot snapshot,
        ILogger<NyxIdAssistantActionRegistryStartupService>? logger,
        TimeSpan startupRetryDelay,
        TimeSpan recoveryRetryDelay)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _logger = logger ?? NullLogger<NyxIdAssistantActionRegistryStartupService>.Instance;
        _startupRetryDelay = startupRetryDelay;
        _recoveryRetryDelay = recoveryRetryDelay;
    }

    internal Task RecoveryCompletion => _recoveryTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= StartupFetchAttempts; attempt++)
        {
            try
            {
                _snapshot.Initialize(await LoadOnceAsync(cancellationToken).ConfigureAwait(false));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogWarning(
                    "NyxID Assistant action registry startup fetch attempt {Attempt}/{Attempts} failed ({FailureType})",
                    attempt,
                    StartupFetchAttempts,
                    ex.GetType().Name);
            }

            if (attempt < StartupFetchAttempts)
                await Task.Delay(_startupRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogError(
            "NyxID Assistant action registry startup failed after {Attempts} attempts; Assistant actions stay disabled until a registry fetch succeeds",
            StartupFetchAttempts);
        _snapshot.Initialize(NyxIdAssistantActionRegistry.CreateDisabled());
        _recoveryTask = Task.Run(() => RecoverAsync(_recoveryStopSource.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _recoveryStopSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await _recoveryTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _recoveryStopSource.Dispose();

    private async Task<NyxIdAssistantActionRegistry> LoadOnceAsync(CancellationToken ct)
    {
        var json = await _source.FetchAsync(ct).ConfigureAwait(false);
        var registry = NyxIdAssistantActionRegistry.Load(json);
        foreach (var skip in registry.SkippedActions)
        {
            _logger.LogWarning(
                "NyxID Assistant action {WireAction} was skipped ({Code}); the remaining actions stay enabled",
                skip.WireAction,
                skip.Code);
        }

        return registry;
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        var delay = _recoveryRetryDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                if (_snapshot.TryUpgrade(await LoadOnceAsync(ct).ConfigureAwait(false)))
                {
                    _logger.LogInformation(
                        "NyxID Assistant action registry recovered after startup failure; Assistant actions are enabled");
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "NyxID Assistant action registry recovery fetch failed ({FailureType})",
                    ex.GetType().Name);
                delay = TimeSpan.FromTicks(
                    Math.Min(delay.Ticks * 2, RecoveryRetryDelayCeiling.Ticks));
            }
        }
    }
}
