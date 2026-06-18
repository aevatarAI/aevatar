using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Resolves the bot's own Lark <c>open_id</c> via the NyxID proxy. The proxy injects the bot app's
/// tenant credentials behind <paramref>providerSlug</paramref>, so any valid caller access token
/// drives a bot-scoped <c>GET /open-apis/bot/v3/info</c> whose payload carries the bot identity.
/// </summary>
public sealed class LarkBotIdentityResolver : ILarkBotIdentityResolver
{
    private const string BotInfoPath = "/open-apis/bot/v3/info";

    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<LarkBotIdentityResolver> _logger;

    public LarkBotIdentityResolver(NyxIdApiClient nyxClient, ILogger<LarkBotIdentityResolver> logger)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> ResolveBotOpenIdAsync(string providerSlug, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerSlug) || string.IsNullOrWhiteSpace(accessToken))
            return null;

        try
        {
            var response = await _nyxClient.ProxyRequestAsync(
                accessToken,
                providerSlug,
                BotInfoPath,
                "GET",
                body: null,
                extraHeaders: null,
                ct);

            return TryParseBotOpenId(response, out var openId) ? openId : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail open: the caller (group-chat gate) engages when identity is unknown. Surface at
            // Warning because a persistent failure silently degrades the gate into over-engaging.
            _logger.LogWarning(ex, "Resolving Lark bot open_id failed: provider={ProviderSlug}", providerSlug);
            return null;
        }
    }

    // Lark bot/v3/info returns {"code":0,"msg":"...","bot":{"open_id":"ou_...",...}} on HTTP 200; some
    // proxy paths nest the payload under "data". Any error envelope ({"code":<non-zero>} or
    // {"error":true,...}) simply lacks a bot.open_id and yields false.
    public static bool TryParseBotOpenId(string? response, out string openId)
    {
        openId = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (TryReadBotOpenId(root, out openId))
                return true;

            return root.TryGetProperty("data", out var data) &&
                   data.ValueKind == JsonValueKind.Object &&
                   TryReadBotOpenId(data, out openId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadBotOpenId(JsonElement container, out string openId)
    {
        openId = string.Empty;
        if (!container.TryGetProperty("bot", out var bot) || bot.ValueKind != JsonValueKind.Object)
            return false;

        if (!bot.TryGetProperty("open_id", out var openIdProperty) ||
            openIdProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = openIdProperty.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        openId = value.Trim();
        return true;
    }
}
