using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Manages pairing between platform senders and bot owners.
/// Persists to chrono-storage so pairings survive restarts.
/// In-memory cache for fast lookups; syncs to chrono-storage on writes.
/// </summary>
public sealed class NyxIdRelayPairingStore
{
    private const string PairingFileName = "relay-pairings.json";
    private const string PairingPrefix = "relay";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ChronoStorageCatalogBlobClient _blobClient;
    private readonly ConnectorCatalogStorageOptions _storageOptions;
    private readonly ILogger _logger;

    // In-memory cache: loaded from chrono-storage on first access.
    private readonly ConcurrentDictionary<string, PairedSender> _paired = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PairingRequest> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private volatile bool _loaded;

    public NyxIdRelayPairingStore(
        ChronoStorageCatalogBlobClient blobClient,
        IOptions<ConnectorCatalogStorageOptions> storageOptions,
        ILogger<NyxIdRelayPairingStore>? logger = null)
    {
        _blobClient = blobClient;
        _storageOptions = storageOptions.Value;
        _logger = logger ?? NullLogger<NyxIdRelayPairingStore>.Instance;
    }

    /// <summary>Check if a sender is paired for a given scope + platform.</summary>
    public async Task<bool> IsPairedAsync(string scopeId, string platform, string senderPlatformId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var key = BuildKey(scopeId, platform, senderPlatformId);
        return _paired.ContainsKey(key);
    }

    /// <summary>
    /// Create a pairing request for an unpaired sender.
    /// Returns the pairing code (or existing code if already pending).
    /// </summary>
    public async Task<string> CreatePairingRequestAsync(
        string scopeId, string platform, string senderPlatformId, string? senderDisplayName,
        string conversationPlatformId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        // Check if already pending
        foreach (var kvp in _pending)
        {
            if (string.Equals(kvp.Value.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(kvp.Value.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                kvp.Value.SenderPlatformId == senderPlatformId &&
                kvp.Value.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return kvp.Key;
            }
        }

        var code = GeneratePairingCode();
        _pending[code] = new PairingRequest
        {
            ScopeId = scopeId,
            Platform = platform,
            SenderPlatformId = senderPlatformId,
            SenderDisplayName = senderDisplayName,
            ConversationPlatformId = conversationPlatformId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        };

        // Pending requests don't need to be persisted (they expire)
        return code;
    }

    /// <summary>
    /// Approve a pairing request by code. Returns the request details, or null if not found/expired.
    /// </summary>
    public async Task<PairingRequest?> ApprovePairingAsync(string pairingCode, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        if (!_pending.TryRemove(pairingCode, out var request))
            return null;

        if (request.ExpiresAt < DateTimeOffset.UtcNow)
            return null;

        var key = BuildKey(request.ScopeId, request.Platform, request.SenderPlatformId);
        _paired[key] = new PairedSender
        {
            ScopeId = request.ScopeId,
            Platform = request.Platform,
            SenderPlatformId = request.SenderPlatformId,
            SenderDisplayName = request.SenderDisplayName,
            PairedAt = DateTimeOffset.UtcNow,
        };

        await PersistAsync(ct);
        return request;
    }

    /// <summary>Remove a paired sender.</summary>
    public async Task<bool> UnpairAsync(string scopeId, string platform, string senderPlatformId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var key = BuildKey(scopeId, platform, senderPlatformId);
        var removed = _paired.TryRemove(key, out _);
        if (removed)
            await PersistAsync(ct);
        return removed;
    }

    /// <summary>List all paired senders for a scope.</summary>
    public async Task<IReadOnlyList<PairedSender>> ListPairedAsync(string scopeId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _paired.Values
            .Where(p => string.Equals(p.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>List pending pairing requests for a scope.</summary>
    public IReadOnlyList<PendingPairingInfo> ListPending(string scopeId)
    {
        var now = DateTimeOffset.UtcNow;
        return _pending
            .Where(kvp => string.Equals(kvp.Value.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase)
                          && kvp.Value.ExpiresAt > now)
            .Select(kvp => new PendingPairingInfo
            {
                Code = kvp.Key,
                Platform = kvp.Value.Platform,
                SenderPlatformId = kvp.Value.SenderPlatformId,
                SenderDisplayName = kvp.Value.SenderDisplayName,
                ExpiresAt = kvp.Value.ExpiresAt,
            })
            .ToList()
            .AsReadOnly();
    }

    // ─── Persistence ───

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;

        await _ioLock.WaitAsync(ct);
        try
        {
            if (_loaded) return; // double-check after acquiring lock

            try
            {
                var context = _blobClient.TryResolveContext(PairingPrefix, PairingFileName);
                if (context != null)
                {
                    var data = await _blobClient.TryDownloadAsync(context, ct);
                    if (data != null)
                    {
                        var state = JsonSerializer.Deserialize<PairingState>(data, JsonOptions);
                        if (state?.Paired != null)
                        {
                            foreach (var p in state.Paired)
                            {
                                var key = BuildKey(p.ScopeId, p.Platform, p.SenderPlatformId);
                                _paired[key] = p;
                            }
                        }

                        _logger.LogInformation("Loaded {Count} paired senders from chrono-storage", _paired.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load pairing state from chrono-storage");
            }

            _loaded = true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var context = _blobClient.TryResolveContext(PairingPrefix, PairingFileName);
            if (context is null) return;

            var state = new PairingState { Paired = _paired.Values.ToList() };
            var json = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            await _blobClient.UploadAsync(context, json, "application/json", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist pairing state to chrono-storage");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ─── Helpers ───

    private static string BuildKey(string scopeId, string platform, string senderPlatformId) =>
        $"{scopeId.Trim().ToLowerInvariant()}:{platform.ToLowerInvariant()}:{senderPlatformId}";

    private static string GeneratePairingCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        return $"PAIR-{Convert.ToHexStringLower(bytes)}";
    }

    // ─── Models ───

    private sealed class PairingState
    {
        public List<PairedSender> Paired { get; set; } = [];
    }

    public sealed class PairedSender
    {
        public string ScopeId { get; set; } = "";
        public string Platform { get; set; } = "";
        public string SenderPlatformId { get; set; } = "";
        public string? SenderDisplayName { get; set; }
        public DateTimeOffset PairedAt { get; set; }
    }

    public sealed class PairingRequest
    {
        public string ScopeId { get; set; } = "";
        public string Platform { get; set; } = "";
        public string SenderPlatformId { get; set; } = "";
        public string? SenderDisplayName { get; set; }
        public string ConversationPlatformId { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public sealed class PendingPairingInfo
    {
        public string Code { get; set; } = "";
        public string Platform { get; set; } = "";
        public string SenderPlatformId { get; set; } = "";
        public string? SenderDisplayName { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
