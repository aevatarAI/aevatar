using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Extensions.AI;

/// <summary>
/// Brokers the OpenAI Realtime provider credential through NyxID: mints a
/// short-lived ephemeral client secret via the NyxID proxy using the caller's
/// per-request NyxID access token (from <see cref="AgentToolRequestContext" />,
/// set by the voice host before connect). The long-lived OpenAI key lives only
/// in the NyxID service; aevatar connects directly to OpenAI with the ephemeral,
/// so NyxID is never in the audio hot path (ADR-013) and aevatar holds no
/// long-lived provider secret (zero-secret-material, ADR-0018). See ADR-0033.
/// </summary>
// Refactor (cluster-voice-nyxid-ephemeral-broker):
//   Old pattern: VoiceProviderConfig.ApiKey carried a static OPENAI_API_KEY; aevatar held the provider key.
//   New principle: the provider key is brokered per session from NyxID into a short-lived ephemeral, on
//     the caller's NyxID identity — consistent with aevatar's per-caller-token NyxID model (no service secret).
public sealed class NyxIdRealtimeProviderCredentialResolver : IRealtimeProviderCredentialResolver
{
    private readonly INyxIdApiClientFactory _clientFactory;
    private readonly NyxIdRealtimeProviderCredentialOptions _options;
    private readonly ILogger<NyxIdRealtimeProviderCredentialResolver> _logger;

    public NyxIdRealtimeProviderCredentialResolver(
        INyxIdApiClientFactory clientFactory,
        NyxIdRealtimeProviderCredentialOptions options,
        ILogger<NyxIdRealtimeProviderCredentialResolver> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> ResolveApiKeyAsync(
        VoiceProviderSessionKey sessionKey,
        VoiceProviderConfig config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(config);

        // Primary source is AgentToolRequestContext (set on the /ws/voice connect and chat paths);
        // the actor-side voice tool-result reconnect runs under VoiceCallerCredentialScope instead.
        var callerToken = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(callerToken))
            callerToken = VoiceCallerCredentialScope.CurrentNyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(callerToken))
        {
            // No per-request NyxID credential in context (e.g. a connect path that
            // is not driven by an authenticated caller). Fall back to the static
            // config key instead of failing here; pure broker deployments (no
            // static key) surface the missing key at provider validation.
            _logger.LogWarning(
                "Realtime provider credential brokering skipped for session {SessionId}: no caller NyxID token in tool-request context.",
                sessionKey.SessionId);
            return null;
        }

        var model = string.IsNullOrWhiteSpace(config.Model) ? _options.Model : config.Model;
        var requestBody = JsonSerializer.Serialize(new
        {
            session = new { type = "realtime", model },
        });

        string responseJson;
        try
        {
            var client = _clientFactory.CreateClient();
            responseJson = await client.ProxyRequestAsync(
                callerToken,
                _options.ServiceSlug,
                _options.MintPath,
                "POST",
                requestBody,
                extraHeaders: null,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RealtimeProviderCredentialException(
                $"Failed to mint an OpenAI realtime ephemeral credential via NyxID service '{_options.ServiceSlug}'.",
                ex);
        }

        var ephemeral = ExtractEphemeralSecret(responseJson);
        if (string.IsNullOrWhiteSpace(ephemeral))
        {
            throw new RealtimeProviderCredentialException(
                $"NyxID service '{_options.ServiceSlug}' returned no ephemeral client secret for the realtime session.");
        }

        _logger.LogDebug(
            "Brokered realtime provider ephemeral credential for session {SessionId} via NyxID service {ServiceSlug}.",
            sessionKey.SessionId,
            _options.ServiceSlug);
        return ephemeral;
    }

    // OpenAI GA POST /v1/realtime/client_secrets returns { "value": "ek_...", "expires_at": ... };
    // the older beta POST /v1/realtime/sessions nests it under { "client_secret": { "value": ... } }.
    private static string? ExtractEphemeralSecret(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (root.TryGetProperty("client_secret", out var clientSecret) &&
                clientSecret.ValueKind == JsonValueKind.Object &&
                clientSecret.TryGetProperty("value", out var nestedValue) &&
                nestedValue.ValueKind == JsonValueKind.String)
            {
                return nestedValue.GetString();
            }
        }
        catch (JsonException ex)
        {
            throw new RealtimeProviderCredentialException(
                "NyxID realtime credential mint returned a non-JSON response.", ex);
        }

        return null;
    }
}
