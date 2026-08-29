using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Aevatar.AI.ToolProviders.NyxId;

internal sealed record NyxIdDelegationTokenLeaseResult(
    bool Succeeded,
    string? AccessToken = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static NyxIdDelegationTokenLeaseResult Success(string accessToken) =>
        new(true, accessToken);

    public static NyxIdDelegationTokenLeaseResult Failed(
        string errorCode,
        string errorMessage) =>
        new(false, ErrorCode: errorCode, ErrorMessage: errorMessage);
}

/// <summary>
/// Keeps short-lived NyxID delegation tokens fresh for long-running tool executions.
/// Token payloads are inspected only to schedule refresh; NyxID remains the authority
/// that validates every token and issues its replacement.
/// </summary>
public sealed class NyxIdDelegationTokenLease : IDisposable
{
    internal const string RefreshFailedErrorCode = "NYXID_DELEGATION_REFRESH_FAILED";
    internal const string RefreshFailedErrorMessage =
        "The NyxID delegation credential could not be renewed.";

    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheSlidingExpiration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CacheAbsoluteExpiration = TimeSpan.FromHours(24);
    private const int MaxCachedLeases = 4096;
    private const int MaxJwtPayloadBytes = 16 * 1024;

    private readonly INyxIdApiClientFactory _clientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly bool _disposeCreatedClients;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = MaxCachedLeases,
    });
    private readonly object _cacheSync = new();

    public NyxIdDelegationTokenLease(
        INyxIdApiClientFactory clientFactory,
        TimeProvider timeProvider)
        : this(clientFactory, timeProvider, disposeCreatedClients: true)
    {
    }

    private NyxIdDelegationTokenLease(
        INyxIdApiClientFactory clientFactory,
        TimeProvider timeProvider,
        bool disposeCreatedClients)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _disposeCreatedClients = disposeCreatedClients;
    }

    internal NyxIdDelegationTokenLease(
        NyxIdApiClient client,
        TimeProvider? timeProvider = null)
        : this(
            new FixedNyxIdApiClientFactory(client),
            timeProvider ?? TimeProvider.System,
            disposeCreatedClients: false)
    {
    }

    internal async Task<NyxIdDelegationTokenLeaseResult> ResolveAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var inspection = Inspect(accessToken);
        if (!inspection.IsDelegated)
            return NyxIdDelegationTokenLeaseResult.Success(accessToken);
        if (inspection.ExpiresAt is null)
        {
            return NyxIdDelegationTokenLeaseResult.Failed(
                RefreshFailedErrorCode,
                RefreshFailedErrorMessage);
        }

        var cacheKey = Fingerprint(accessToken);
        var state = GetOrCreateState(cacheKey, accessToken, inspection.ExpiresAt.Value);
        await state.RefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (state.ExpiresAt - now > RefreshBeforeExpiry)
                return NyxIdDelegationTokenLeaseResult.Success(state.AccessToken);

            if (state.LastFailureAt is { } lastFailureAt &&
                now - lastFailureAt < RefreshFailureBackoff)
            {
                return NyxIdDelegationTokenLeaseResult.Failed(
                    RefreshFailedErrorCode,
                    RefreshFailedErrorMessage);
            }

            var refresh = await RefreshAsync(state.AccessToken, ct).ConfigureAwait(false);
            if (!refresh.Succeeded ||
                string.IsNullOrWhiteSpace(refresh.AccessToken) ||
                refresh.ExpiresIn is null or <= 0)
            {
                state.LastFailureAt = now;
                return NyxIdDelegationTokenLeaseResult.Failed(
                    RefreshFailedErrorCode,
                    RefreshFailedErrorMessage);
            }

            var refreshedInspection = Inspect(refresh.AccessToken);
            if (!refreshedInspection.IsDelegated || refreshedInspection.ExpiresAt is null)
            {
                state.LastFailureAt = now;
                return NyxIdDelegationTokenLeaseResult.Failed(
                    RefreshFailedErrorCode,
                    RefreshFailedErrorMessage);
            }

            var responseExpiresAt = now.AddSeconds(refresh.ExpiresIn.Value);
            var expiresAt = refreshedInspection.ExpiresAt.Value < responseExpiresAt
                ? refreshedInspection.ExpiresAt.Value
                : responseExpiresAt;
            if (expiresAt <= now)
            {
                state.LastFailureAt = now;
                return NyxIdDelegationTokenLeaseResult.Failed(
                    RefreshFailedErrorCode,
                    RefreshFailedErrorMessage);
            }

            state.AccessToken = refresh.AccessToken;
            state.ExpiresAt = expiresAt;
            state.LastFailureAt = null;
            return NyxIdDelegationTokenLeaseResult.Success(state.AccessToken);
        }
        finally
        {
            state.RefreshGate.Release();
        }
    }

    public void Dispose() => _cache.Dispose();

    private async Task<NyxIdDelegationRefreshResult> RefreshAsync(
        string accessToken,
        CancellationToken ct)
    {
        var client = _clientFactory.CreateClient();
        try
        {
            return await client.RefreshDelegationAsync(accessToken, ct).ConfigureAwait(false);
        }
        finally
        {
            if (_disposeCreatedClients)
                client.Dispose();
        }
    }

    private LeaseState GetOrCreateState(
        string cacheKey,
        string accessToken,
        DateTimeOffset expiresAt)
    {
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(cacheKey, out LeaseState? existing) && existing is not null)
                return existing;

            var state = new LeaseState(accessToken, expiresAt);
            _cache.Set(
                cacheKey,
                state,
                new MemoryCacheEntryOptions
                {
                    SlidingExpiration = CacheSlidingExpiration,
                    AbsoluteExpirationRelativeToNow = CacheAbsoluteExpiration,
                    Size = 1,
                });
            return state;
        }
    }

    private static DelegationTokenInspection Inspect(string accessToken)
    {
        var firstSeparator = accessToken.IndexOf('.');
        if (firstSeparator <= 0)
            return default;
        var secondSeparator = accessToken.IndexOf('.', firstSeparator + 1);
        if (secondSeparator <= firstSeparator + 1 ||
            accessToken.IndexOf('.', secondSeparator + 1) >= 0)
        {
            return default;
        }

        var payload = accessToken.AsSpan(firstSeparator + 1, secondSeparator - firstSeparator - 1);
        if (payload.Length == 0 || payload.Length > MaxJwtPayloadBytes * 2)
            return default;

        try
        {
            var decoded = DecodeBase64Url(payload);
            if (decoded.Length > MaxJwtPayloadBytes)
                return default;

            using var document = JsonDocument.Parse(decoded);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("delegated", out var delegated) ||
                delegated.ValueKind != JsonValueKind.True)
            {
                return default;
            }

            if (!root.TryGetProperty("exp", out var exp) ||
                exp.ValueKind != JsonValueKind.Number ||
                !exp.TryGetInt64(out var expiresAtUnixSeconds))
            {
                return new DelegationTokenInspection(true, null);
            }

            try
            {
                return new DelegationTokenInspection(
                    true,
                    DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds));
            }
            catch (ArgumentOutOfRangeException)
            {
                return new DelegationTokenInspection(true, null);
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return default;
        }
    }

    private static byte[] DecodeBase64Url(ReadOnlySpan<char> payload)
    {
        var padding = (4 - payload.Length % 4) % 4;
        var encoded = string.Create(
            payload.Length + padding,
            (Payload: payload.ToString(), Padding: padding),
            static (buffer, state) =>
            {
                for (var index = 0; index < state.Payload.Length; index++)
                {
                    buffer[index] = state.Payload[index] switch
                    {
                        '-' => '+',
                        '_' => '/',
                        var character => character,
                    };
                }

                for (var index = state.Payload.Length; index < buffer.Length; index++)
                    buffer[index] = '=';
            });
        return Convert.FromBase64String(encoded);
    }

    private static string Fingerprint(string accessToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));

    private sealed class LeaseState(string accessToken, DateTimeOffset expiresAt)
    {
        public SemaphoreSlim RefreshGate { get; } = new(1, 1);
        public string AccessToken { get; set; } = accessToken;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public DateTimeOffset? LastFailureAt { get; set; }
    }

    private readonly record struct DelegationTokenInspection(
        bool IsDelegated,
        DateTimeOffset? ExpiresAt);

    private sealed class FixedNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }
}
