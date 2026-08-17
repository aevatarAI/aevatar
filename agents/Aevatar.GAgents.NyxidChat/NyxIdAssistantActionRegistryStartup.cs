using System.Collections.Frozen;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

internal interface INyxIdAssistantActionRegistrySource
{
    Task<string> FetchAsync(CancellationToken ct);
}

internal enum NyxIdAssistantActionRegistryReadinessStatus
{
    Disabled = 1,
    Unavailable = 2,
    Ready = 3,
    Partial = 4,
}

internal sealed record NyxIdAssistantActionRegistryReadinessSnapshot(
    NyxIdAssistantActionRegistryReadinessStatus Status,
    int SchemaVersion,
    string RegistryRevision,
    FrozenDictionary<string, NyxIdAssistantActionCapabilityReadinessSnapshot> Actions,
    string FailureCode)
{
    public const string UnavailableFailureCode = "NYXID_ACTION_REGISTRY_UNAVAILABLE";

    public static NyxIdAssistantActionRegistryReadinessSnapshot Disabled() =>
        new(
            NyxIdAssistantActionRegistryReadinessStatus.Disabled,
            0,
            string.Empty,
            new Dictionary<string, NyxIdAssistantActionCapabilityReadinessSnapshot>(
                    StringComparer.Ordinal)
                .ToFrozenDictionary(StringComparer.Ordinal),
            string.Empty);

    public static NyxIdAssistantActionRegistryReadinessSnapshot Unavailable(
        string failureCode) =>
        new(
            NyxIdAssistantActionRegistryReadinessStatus.Unavailable,
            0,
            string.Empty,
            new Dictionary<string, NyxIdAssistantActionCapabilityReadinessSnapshot>(
                    StringComparer.Ordinal)
                .ToFrozenDictionary(StringComparer.Ordinal),
            string.IsNullOrWhiteSpace(failureCode)
                ? UnavailableFailureCode
                : failureCode);

    public static NyxIdAssistantActionRegistryReadinessSnapshot FromRegistry(
        NyxIdAssistantActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var status = registry.CapabilityReadiness.Values.All(static action => action.Executable)
            ? NyxIdAssistantActionRegistryReadinessStatus.Ready
            : NyxIdAssistantActionRegistryReadinessStatus.Partial;
        return new NyxIdAssistantActionRegistryReadinessSnapshot(
            status,
            registry.SchemaVersion,
            registry.RegistryRevision,
            registry.CapabilityReadiness,
            string.Empty);
    }
}

internal sealed record NyxIdAssistantActionRegistryStartupSnapshot(
    NyxIdAssistantActionRegistry Registry,
    NyxIdAssistantActionRegistryReadinessSnapshot Readiness);

/// <summary>
/// One-shot process configuration initialized before the Host accepts work.
/// This is an immutable external contract snapshot, not conversation or action
/// runtime state, and intentionally exposes no refresh or replacement API.
/// </summary>
internal sealed class NyxIdAssistantActionRegistrySnapshot
{
    private NyxIdAssistantActionRegistryStartupSnapshot? _snapshot;

    public void Initialize(NyxIdAssistantActionRegistry registry) =>
        Initialize(
            registry,
            NyxIdAssistantActionRegistryReadinessSnapshot.FromRegistry(registry));

    public void Initialize(
        NyxIdAssistantActionRegistry registry,
        NyxIdAssistantActionRegistryReadinessSnapshot readiness)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(readiness);
        var snapshot = new NyxIdAssistantActionRegistryStartupSnapshot(registry, readiness);
        if (Interlocked.CompareExchange(ref _snapshot, snapshot, null) is not null)
        {
            throw new InvalidOperationException(
                "The NyxID Assistant action registry startup snapshot is already initialized.");
        }
    }

    public NyxIdAssistantActionRegistry GetRequired() =>
        GetSnapshotRequired().Registry;

    public NyxIdAssistantActionRegistryReadinessSnapshot GetReadinessRequired() =>
        GetSnapshotRequired().Readiness;

    private NyxIdAssistantActionRegistryStartupSnapshot GetSnapshotRequired() =>
        Volatile.Read(ref _snapshot) ?? throw new InvalidOperationException(
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

internal sealed class NyxIdAssistantActionRegistryStartupService : IHostedService
{
    private readonly INyxIdAssistantActionRegistrySource _source;
    private readonly NyxIdAssistantActionRegistrySnapshot _snapshot;
    private readonly NyxIdAssistantActionsOptions _options;
    private readonly ILogger<NyxIdAssistantActionRegistryStartupService> _logger;

    public NyxIdAssistantActionRegistryStartupService(
        INyxIdAssistantActionRegistrySource source,
        NyxIdAssistantActionRegistrySnapshot snapshot,
        NyxIdAssistantActionsOptions options,
        ILogger<NyxIdAssistantActionRegistryStartupService>? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<NyxIdAssistantActionRegistryStartupService>.Instance;
    }

    public NyxIdAssistantActionRegistryStartupService(
        INyxIdAssistantActionRegistrySource source,
        NyxIdAssistantActionRegistrySnapshot snapshot,
        ILogger<NyxIdAssistantActionRegistryStartupService>? logger = null)
        : this(source, snapshot, new NyxIdAssistantActionsOptions(), logger)
    {
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await _source.FetchAsync(cancellationToken).ConfigureAwait(false);
            var registry = NyxIdAssistantActionRegistry.Load(json);
            _snapshot.Initialize(registry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failureCode = exception switch
            {
                NyxIdAssistantActionRegistryException registryException => registryException.Code,
                NyxIdActionSecretPolicyException secretPolicyException => secretPolicyException.Code,
                _ => NyxIdAssistantActionRegistryReadinessSnapshot.UnavailableFailureCode,
            };
            _snapshot.Initialize(
                NyxIdAssistantActionRegistry.CreateDisabled(),
                NyxIdAssistantActionRegistryReadinessSnapshot.Unavailable(failureCode));
            _logger.LogError(
                "NyxID Assistant action registry startup failed ({FailureType}); Assistant actions are disabled for this process",
                exception.GetType().Name);
            if (_options.Required)
                throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
