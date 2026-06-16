using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Execute shell commands on remote SSH-typed NyxID services. The HTTP proxy
/// (<see cref="NyxIdProxyTool"/>) cannot reach SSH endpoints — those services
/// are registered as <c>ssh://host:port</c> and require this dedicated tool.
/// </summary>
public sealed class NyxIdSshExecTool : IAgentTool
{
    private const int DefaultTimeoutSecs = 30;
    private const int MaxTimeoutSecs = 300;

    private readonly NyxIdApiClient _client;
    private readonly NyxIdToolOptions _options;
    private readonly ILogger _logger;

    public NyxIdSshExecTool(NyxIdApiClient client, NyxIdToolOptions? options = null, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new NyxIdToolOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name => "ssh_exec";

    public string Description =>
        "Execute a shell command on a remote SSH host via a NyxID-bound SSH service. " +
        "The target service must be SSH-typed (its endpoint starts with 'ssh://'); " +
        "HTTP services use 'nyxid_proxy' instead. Use 'nyxid_proxy' (no slug) or " +
        "'nyxid_services' to discover services and read their endpoint scheme. " +
        "NyxID enforces an 8 KiB command length, a 1 MiB stdout/stderr cap, a 300s " +
        "timeout, and blocks dangerous commands (rm -rf /, mkfs, dd if=, fork bombs).";

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    /// <summary>
    /// SSH execution can mutate the remote host arbitrarily. Hosts may explicitly bypass
    /// this local approval gate only for internal-only deployments with their own trust boundary.
    /// </summary>
    public bool? RequiresApproval(string argumentsJson) => !_options.BypassSshExecApproval;

    public string ParametersSchema => """
        {
          "type": "object",
          "required": ["service", "command", "principal"],
          "properties": {
            "service": {
              "type": "string",
              "description": "NyxID service slug or UUID. Must be SSH-typed (endpoint scheme 'ssh://')."
            },
            "command": {
              "type": "string",
              "description": "Shell command to run on the remote host. Max 8192 chars."
            },
            "principal": {
              "type": "string",
              "description": "Unix username on the remote host (e.g. 'ubuntu', 'root')."
            },
            "timeout_secs": {
              "type": "integer",
              "minimum": 1,
              "maximum": 300,
              "description": "Max execution time in seconds. Defaults to 30, capped at 300."
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError)
            return $"{{\"error\":\"Failed to parse tool arguments\",\"detail\":{JsonSerializer.Serialize(args.ParseError)}}}";

        var service = args.Str("service") ?? args.Str("slug");
        var command = args.Str("command");
        var principal = args.Str("principal");
        var timeoutSecs = ParseTimeoutSecs(args.Str("timeout_secs"));

        if (string.IsNullOrWhiteSpace(service) ||
            string.IsNullOrWhiteSpace(command) ||
            string.IsNullOrWhiteSpace(principal))
        {
            return """{"error":"'service', 'command', and 'principal' are required."}""";
        }

        // NyxID's /api/v1/ssh/{id}/exec route keys on `catalog_service_id`, NOT on the user
        // service slug or user-service id. The CLI's `nyxid ssh exec` does the same hop
        // internally (cli/src/commands/ssh.rs:resolve_ssh_service_id). Without this resolve,
        // a slug like 'sg-office-network' or its user-service uuid both 404 with NyxID's
        // generic "Service not found" envelope, and the LLM gets stuck retrying the same
        // wrong path.
        var catalogServiceId = await ResolveCatalogServiceIdAsync(token, service, ct);

        _logger.LogInformation(
            "[ssh_exec] service={Service} catalogId={CatalogId} principal={Principal} timeoutSecs={Timeout}",
            service, catalogServiceId, principal, timeoutSecs);

        var body = JsonSerializer.Serialize(new
        {
            command,
            principal,
            timeout_secs = timeoutSecs,
        });

        // Hard wall-clock cap: NyxID's HTTP response *should* arrive within
        // `timeout_secs + a few seconds` (the server-side timer kicks in and returns
        // `timed_out: true`). In practice we have observed the call hang well past 60s
        // (NyxID SSH gateway / NodeAgent stuck on a stale session). Without a hard cap,
        // the LLM run sits on a single tool call long enough to blow the run actor's
        // turn budget and the user gets the generic "took too long" fallback instead of
        // a usable error from this tool. Cap at `timeout_secs + 15s` so NyxID has a
        // generous margin to return its own timeout response, then fail this tool with
        // a clear "ssh_timeout" payload that the LLM can summarize for the user.
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
                timeoutSecs + 15, service, catalogServiceId);
            return $$"""{"error":"ssh_timeout","detail":"NyxID did not return an SSH exec response within {{timeoutSecs + 15}}s. The remote host or NyxID gateway is unresponsive; try again or pick a different host."}""";
        }
    }

    /// <summary>
    /// Resolve a slug or user-service id into the catalog_service_id required by NyxID's SSH
    /// route. Mirrors the CLI's <c>resolve_ssh_service_id</c>: prefer the user-service entry's
    /// <c>catalog_service_id</c>, otherwise fall back to the input (so a raw catalog id passed
    /// directly still works).
    /// </summary>
    private async Task<string> ResolveCatalogServiceIdAsync(
        string token, string serviceIdOrSlug, CancellationToken ct)
    {
        try
        {
            // Direct lookup by user-service id or slug — NyxID's /keys/{x} accepts either.
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
                ex, "[ssh_exec] direct /keys/{Service} lookup failed; falling back to list", serviceIdOrSlug);
        }

        try
        {
            // List + match by slug — covers cases where direct lookup returns a wrapper without
            // the field surfaced at the top level.
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

        // Caller passed a raw catalog id directly (the CLI also falls through this way).
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
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            // NyxID's /keys/{x} returns either the entry directly or wrapped in { error: ... }.
            if (root.TryGetProperty("error", out _))
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

    private static int ParseTimeoutSecs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultTimeoutSecs;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            return DefaultTimeoutSecs;
        }
        return Math.Clamp(v, 1, MaxTimeoutSecs);
    }
}
