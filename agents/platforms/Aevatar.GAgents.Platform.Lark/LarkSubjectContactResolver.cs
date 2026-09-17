using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Platform.Lark;

public class LarkSubjectContactResolver : IChannelSubjectContactResolver
{
    public virtual string Platform => "lark";
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<LarkSubjectContactResolver> _logger;
    private readonly IChannelRelayProxyResponseClassifier _classifier;
    public LarkSubjectContactResolver(NyxIdApiClient client, ILogger<LarkSubjectContactResolver> logger,
        IChannelRelayProxyResponseClassifier classifier) { _nyxClient = client; _logger = logger; _classifier = classifier; }
    private ChannelRelayProxyResponseClassification ClassifyRelayProxyResponse(string? response) => _classifier.Classify(response);
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? TryReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? NormalizeOptional(value.GetString()) : null;
    public async Task<ChannelSubjectContactIds?> ResolveAsync(
        ChannelInboundEvent inboundEvent,
        ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext,
        string? larkUnionId,
        CancellationToken ct)
    {
        if (activity?.Type != ActivityType.Message)
            return null;

        var accessToken = activity is null
            ? NormalizeOptional(runtimeContext.NyxUserAccessToken)
            : (NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken) ?? NormalizeOptional(runtimeContext.NyxUserAccessToken));
        var providerSlug = NormalizeOptional(inboundEvent.NyxProviderSlug);
        var scopeId = NormalizeOptional(inboundEvent.RegistrationScopeId);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(providerSlug) ||
            string.IsNullOrWhiteSpace(scopeId))
        {
            return null;
        }

        var lookupId = NormalizeOptional(larkUnionId);
        var userIdType = "union_id";
        if (string.IsNullOrWhiteSpace(lookupId))
        {
            lookupId = NormalizeOptional(inboundEvent.SenderId);
            userIdType = "open_id";
        }

        if (string.IsNullOrWhiteSpace(lookupId))
            return null;

        try
        {
            var response = await _nyxClient.ProxyRequestAsync(
                    accessToken!,
                    providerSlug!,
                    $"/open-apis/contact/v3/users/{Uri.EscapeDataString(lookupId!)}?user_id_type={userIdType}",
                    "GET",
                    body: null,
                    extraHeaders: null,
                    ct)
                .ConfigureAwait(false);

            if (ClassifyRelayProxyResponse(response).IsError)
                return null;

            return TryParseChannelSubjectContactIds(response, out var contactIds) ? contactIds : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lark subject contact lookup failed open: provider={ProviderSlug}, userIdType={UserIdType}",
                providerSlug,
                userIdType);
            return null;
        }
    }

    private static bool TryParseChannelSubjectContactIds(string? response, out ChannelSubjectContactIds contactIds)
    {
        contactIds = new ChannelSubjectContactIds(null, null);
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("user", out var user) ||
                user.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var userId = TryReadString(user, "user_id");
            var employeeId = TryReadString(user, "employee_id");
            if (string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(employeeId))
                return false;

            contactIds = new ChannelSubjectContactIds(userId, employeeId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }


}

/// <summary>Explicit Feishu identity for the existing Lark protocol behavior.</summary>
public sealed class FeishuSubjectContactResolver(NyxIdApiClient client, ILogger<LarkSubjectContactResolver> logger, IChannelRelayProxyResponseClassifier classifier) : LarkSubjectContactResolver(client, logger, classifier)
{
    public override string Platform => "feishu";
}
