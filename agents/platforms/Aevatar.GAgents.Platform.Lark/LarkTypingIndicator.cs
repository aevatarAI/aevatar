using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Platform.Lark;

public class LarkTypingIndicator : IChannelTypingIndicator
{
    public virtual string Platform => "lark";
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<LarkTypingIndicator> _logger;
    private readonly IChannelRelayProxyResponseClassifier _classifier;
    public LarkTypingIndicator(NyxIdApiClient nyxClient, ILogger<LarkTypingIndicator> logger,
        IChannelRelayProxyResponseClassifier classifier)
    { _nyxClient = nyxClient; _logger = logger; _classifier = classifier; }
    private ChannelRelayProxyResponseClassification ClassifyRelayProxyResponse(string? response) => _classifier.Classify(response);
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    // Lark reaction emoji_type for "hands typing on keyboard" — added immediately on inbound
    // so the user sees the bot is working before the LLM reply lands. After a reply succeeds,
    // the reaction is cleared instead of replaced with DONE because DONE reads as task completion,
    // while a chat reply can be an intermediate progress update.
    private const string TypingReactionEmojiType = "Typing";

    public async Task StartAsync(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        if (!ShouldSendImmediateLarkReaction(activity, registration, out var accessToken, out var providerSlug, out var platformMessageId))
            return;

        try
        {
            var response = await _nyxClient.ProxyRequestAsync(
                accessToken!,
                providerSlug!,
                $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions",
                "POST",
                $$$"""{"reaction_type":{"emoji_type":"{{{TypingReactionEmojiType}}}"}}""",
                null,
                ct);

            var classification = ClassifyRelayProxyResponse(response);
            if (classification.IsError)
            {
                if (classification.Kind == ChannelRelayProxyResponseKind.PermissionDenied)
                {
                    // The bot is missing reaction permission on Lark — a
                    // tenant-level config issue that recurs on every inbound
                    // message until ops fixes the app scope. Log at Debug so
                    // it stays discoverable when the channel is opted into
                    // verbose logging without spamming Warnings on every turn.
                    _logger.LogDebug(
                        "Immediate Lark typing reaction skipped (missing reaction scope): provider={ProviderSlug}, message={MessageId}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        classification.Detail);
                }
                else
                {
                    // Anything else is a real signal that should stay at Warning
                    // so provider behavior changes remain visible.
                    _logger.LogWarning(
                        "Immediate Lark typing reaction failed: provider={ProviderSlug}, message={MessageId}, providerErrorCode={ProviderErrorCode}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        classification.ProviderErrorCode,
                        classification.Detail);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Immediate Lark typing reaction threw: provider={ProviderSlug}, message={MessageId}",
                providerSlug,
                platformMessageId);
        }
    }

    // After a successful reply, remove the bot's "Typing" reaction. Uses list-based discovery (filter by
    // emoji_type=Typing AND operator_type=app) instead of caching the immediate reaction's
    // reaction_id locally — the runner is a singleton and cross-turn state on it would violate the
    // "中间层进程内缓存作为事实源" rule. Filtering on operator_type=app avoids deleting any user
    // who happened to add the same Typing reaction.
    public async Task ClearAsync(
        InboundMessage inbound,
        ChannelBotRegistrationEntry? registration,
        CancellationToken ct)
    {
        if (registration is null)
            return;

        if (!ShouldClearTypingReaction(inbound, registration, out var accessToken, out var providerSlug, out var platformMessageId))
            return;

        try
        {
            var reactionIds = new List<string>();
            string? pageToken = null;
            // Bound the iteration so a misbehaving Lark response (e.g. always-true `has_more`)
            // can't loop the clear forever. 10 pages × 50 per page = 500 Typing reactions on a
            // single message — orders of magnitude more than realistic, since this list is
            // already scoped to one emoji_type and the bot only adds Typing once per inbound.
            const int MaxListPages = 10;
            for (var page = 0; page < MaxListPages; page++)
            {
                var pathQuery = $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions?reaction_type={TypingReactionEmojiType}&page_size=50";
                if (pageToken is not null)
                    pathQuery += $"&page_token={Uri.EscapeDataString(pageToken)}";

                var listResponse = await _nyxClient.ProxyRequestAsync(
                    accessToken!,
                    providerSlug!,
                    pathQuery,
                    "GET",
                    body: null,
                    extraHeaders: null,
                    ct);

                var listClassification = ClassifyRelayProxyResponse(listResponse);
                if (listClassification.IsError)
                {
                    _logger.LogDebug(
                        "Lark typing reaction list failed; skipping clear: provider={ProviderSlug}, message={MessageId}, page={Page}, providerErrorCode={ProviderErrorCode}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        page,
                        listClassification.ProviderErrorCode,
                        listClassification.Detail);
                    return;
                }

                var (idsOnPage, nextPageToken) = ParseAppReactionsPage(listResponse);
                reactionIds.AddRange(idsOnPage);
                if (string.IsNullOrWhiteSpace(nextPageToken))
                {
                    pageToken = null;
                    break;
                }
                pageToken = nextPageToken;
            }

            foreach (var reactionId in reactionIds)
            {
                try
                {
                    var deleteResponse = await _nyxClient.ProxyRequestAsync(
                        accessToken!,
                        providerSlug!,
                        $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions/{Uri.EscapeDataString(reactionId)}",
                        "DELETE",
                        body: null,
                        extraHeaders: null,
                        ct);

                    var deleteClassification = ClassifyRelayProxyResponse(deleteResponse);
                    if (deleteClassification.IsError)
                    {
                        _logger.LogDebug(
                            "Lark typing reaction delete failed: provider={ProviderSlug}, message={MessageId}, reaction={ReactionId}, providerErrorCode={ProviderErrorCode}, detail={Detail}",
                            providerSlug,
                            platformMessageId,
                            reactionId,
                            deleteClassification.ProviderErrorCode,
                            deleteClassification.Detail);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Lark typing reaction delete threw: provider={ProviderSlug}, message={MessageId}, reaction={ReactionId}",
                        providerSlug,
                        platformMessageId,
                        reactionId);
                }
            }

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lark typing reaction clear threw: provider={ProviderSlug}, message={MessageId}",
                providerSlug,
                platformMessageId);
        }
    }

    private static (IReadOnlyList<string> AppReactionIds, string? NextPageToken) ParseAppReactionsPage(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return (Array.Empty<string>(), null);

        try
        {
            return ExtractAppReactionsPage(response);
        }
        catch (JsonException)
        {
            return (Array.Empty<string>(), null);
        }
    }

    private static (List<string> AppReactionIds, string? NextPageToken) ExtractAppReactionsPage(string response)
    {
        var ids = new List<string>();
        string? nextPageToken = null;

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (ids, null);

        if (!root.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Object)
            return (ids, null);

        // Pin pagination to has_more=true. Following page_token unconditionally would let a Lark
        // response that returns a stale token alongside has_more=false re-fetch the same page
        // until the safety cap fires.
        var hasMore = dataProp.TryGetProperty("has_more", out var hasMoreProp) &&
                      hasMoreProp.ValueKind == JsonValueKind.True;
        if (hasMore &&
            dataProp.TryGetProperty("page_token", out var pageTokenProp) &&
            pageTokenProp.ValueKind == JsonValueKind.String)
        {
            var token = pageTokenProp.GetString();
            if (!string.IsNullOrWhiteSpace(token))
                nextPageToken = token;
        }

        if (!dataProp.TryGetProperty("items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array)
            return (ids, nextPageToken);

        foreach (var item in itemsProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            // Only delete reactions added by the bot itself (operator_type=app); leave any
            // user-added Typing reactions alone so the clear doesn't accidentally erase them.
            if (!item.TryGetProperty("operator", out var operatorProp) ||
                operatorProp.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!operatorProp.TryGetProperty("operator_type", out var operatorTypeProp) ||
                operatorTypeProp.ValueKind != JsonValueKind.String ||
                !string.Equals(operatorTypeProp.GetString(), "app", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("reaction_id", out var reactionIdProp) ||
                reactionIdProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var reactionId = reactionIdProp.GetString();
            if (!string.IsNullOrWhiteSpace(reactionId))
                ids.Add(reactionId);
        }

        return (ids, nextPageToken);
    }

    private static bool ShouldClearTypingReaction(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        out string? accessToken,
        out string? providerSlug,
        out string? platformMessageId)
    {
        accessToken = null;
        providerSlug = null;
        platformMessageId = null;

        accessToken = NormalizeOptional(inbound.TransportExtras?.NyxUserAccessToken);
        providerSlug = NormalizeOptional(registration.NyxProviderSlug);
        platformMessageId = NormalizeOptional(inbound.TransportExtras?.NyxPlatformMessageId);

        return !string.IsNullOrWhiteSpace(accessToken) &&
               !string.IsNullOrWhiteSpace(providerSlug) &&
               !string.IsNullOrWhiteSpace(platformMessageId) &&
               platformMessageId.StartsWith("om_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSendImmediateLarkReaction(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        out string? accessToken,
        out string? providerSlug,
        out string? platformMessageId)
    {
        accessToken = null;
        providerSlug = null;
        platformMessageId = null;

        if (activity.Type != ActivityType.Message)
            return false;

        accessToken = NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken);
        providerSlug = NormalizeOptional(registration.NyxProviderSlug);
        platformMessageId = NormalizeOptional(activity.TransportExtras?.NyxPlatformMessageId);

        return !string.IsNullOrWhiteSpace(accessToken) &&
               !string.IsNullOrWhiteSpace(providerSlug) &&
               !string.IsNullOrWhiteSpace(platformMessageId) &&
               platformMessageId.StartsWith("om_", StringComparison.OrdinalIgnoreCase);
    }


}

/// <summary>Explicit Feishu identity for the existing Lark protocol behavior.</summary>
public sealed class FeishuTypingIndicator(NyxIdApiClient client, ILogger<LarkTypingIndicator> logger, IChannelRelayProxyResponseClassifier classifier) : LarkTypingIndicator(client, logger, classifier)
{
    public override string Platform => "feishu";
}
