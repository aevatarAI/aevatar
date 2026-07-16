using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

internal sealed record NyxIdSshCommandRequest(
    string Service,
    string Principal,
    string Command,
    int? TimeoutSecs = null);

internal interface INyxIdSshCommandExecutor
{
    Task<string> ExecuteAsync(NyxIdSshCommandRequest request, CancellationToken ct = default);
}

/// <summary>
/// Typed transport shared by SSH-backed NyxID tools. Service resolution, authentication,
/// timeout enforcement, and the NyxID HTTP contract live only here.
/// </summary>
internal sealed class NyxIdSshCommandExecutor : INyxIdSshCommandExecutor
{
    private const int DefaultTimeoutSecs = 30;
    private const int MaxTimeoutSecs = 300;

    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public NyxIdSshCommandExecutor(NyxIdApiClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<string> ExecuteAsync(
        NyxIdSshCommandRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return JsonSerializer.Serialize(new
            {
                error = "No NyxID access token available. User must be authenticated.",
            });
        }

        var timeoutSecs = Math.Clamp(request.TimeoutSecs ?? DefaultTimeoutSecs, 1, MaxTimeoutSecs);
        var catalogServiceId = await ResolveCatalogServiceIdAsync(token, request.Service, ct);

        _logger.LogInformation(
            "[ssh_exec] service={Service} catalogId={CatalogId} principal={Principal} timeoutSecs={Timeout}",
            request.Service,
            catalogServiceId,
            request.Principal,
            timeoutSecs);

        var body = JsonSerializer.Serialize(new
        {
            command = request.Command,
            principal = request.Principal,
            timeout_secs = timeoutSecs,
        });

        // Keep a hard wall-clock cap around NyxID in case the SSH gateway or node session stalls.
        using var sshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sshCts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs + 15));
        try
        {
            return await _client.SshExecAsync(token, catalogServiceId, body, sshCts.Token);
        }
        catch (OperationCanceledException) when (sshCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[ssh_exec] hard timeout after {WallClockSecs}s waiting on NyxID for service={Service} catalogId={CatalogId}",
                timeoutSecs + 15,
                request.Service,
                catalogServiceId);
            return JsonSerializer.Serialize(new
            {
                error = "ssh_timeout",
                detail = $"NyxID did not return an SSH exec response within {timeoutSecs + 15}s. " +
                         "The remote host or NyxID gateway is unresponsive; try again or pick a different host.",
            });
        }
    }

    /// <summary>
    /// Resolve a slug or user-service id into the catalog_service_id required by NyxID's SSH
    /// route. Mirrors the CLI's resolve_ssh_service_id behavior.
    /// </summary>
    private async Task<string> ResolveCatalogServiceIdAsync(
        string token,
        string serviceIdOrSlug,
        CancellationToken ct)
    {
        try
        {
            var direct = await _client.GetServiceAsync(token, serviceIdOrSlug, ct);
            var catalog = TryReadCatalogServiceId(direct);
            if (!string.IsNullOrWhiteSpace(catalog))
                return catalog;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ssh_exec] direct /keys/{Service} lookup failed; falling back to list",
                serviceIdOrSlug);
        }

        try
        {
            var listJson = await _client.ListServicesAsync(token, ct);
            using var doc = JsonDocument.Parse(listJson);
            var root = doc.RootElement;

            JsonElement entries = default;
            var hasEntries = false;
            if (root.ValueKind == JsonValueKind.Array)
            {
                entries = root;
                hasEntries = true;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("keys", out var keysProp) &&
                     keysProp.ValueKind == JsonValueKind.Array)
            {
                entries = keysProp;
                hasEntries = true;
            }

            if (hasEntries)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!MatchesService(entry, serviceIdOrSlug))
                        continue;
                    if (entry.TryGetProperty("catalog_service_id", out var catalogProp) &&
                        catalogProp.ValueKind == JsonValueKind.String)
                    {
                        var candidate = catalogProp.GetString();
                        if (!string.IsNullOrWhiteSpace(candidate))
                            return candidate;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ssh_exec] /keys list lookup failed for {Service}", serviceIdOrSlug);
        }

        return serviceIdOrSlug;
    }

    private static string? TryReadCatalogServiceId(string keyResponse)
    {
        if (string.IsNullOrWhiteSpace(keyResponse))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(keyResponse);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                return null;
            if (root.TryGetProperty("catalog_service_id", out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool MatchesService(JsonElement entry, string idOrSlug)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var key in new[] { "id", "_id", "slug", "service_slug" })
        {
            if (entry.TryGetProperty(key, out var prop) &&
                prop.ValueKind == JsonValueKind.String &&
                string.Equals(prop.GetString(), idOrSlug, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
