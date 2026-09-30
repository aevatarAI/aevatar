using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.NyxidChat.ExternalCallbacks;

/// <summary>Performs the existing NyxID create only when called by the committed operation authority.</summary>
public sealed class NyxIdConnectLinkCreationAdapter(
    INyxIdApiClientFactory clientFactory,
    INyxIdConnectedServiceCapabilityIssuer capabilityIssuer,
    TimeProvider timeProvider) : IConnectLinkCreationPort
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<ConnectLinkCreationResult> CreateAsync(ExternalCallbackRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ct.ThrowIfCancellationRequested();
        if (registration.Kind != ExternalCallbackKind.ConnectLink || registration.ConnectLinkRequest is null ||
            string.IsNullOrWhiteSpace(registration.RequestedCatalogServiceSlug))
            return new() { FailureCode = "connect_link_registration_invalid", FailureOutcome = ConnectLinkCreationFailureOutcome.NotCreated };
        if (!NyxIdConnectLinkVerifier.HasAuthorization(registration.Authorization))
            return new() { FailureCode = "original_sender_authorization_missing", FailureOutcome = ConnectLinkCreationFailureOutcome.NotCreated };

        CapabilityHandle capability;
        try
        {
            capability = await capabilityIssuer.IssueByBindingIdAsync(
                registration.Authorization.ExternalSubject, registration.Authorization.BindingId, ct).ConfigureAwait(false);
        }
        catch (BindingChangedException) { return new() { FailureCode = "original_binding_changed", FailureOutcome = ConnectLinkCreationFailureOutcome.NotCreated }; }
        catch (BindingRevokedException) { return new() { FailureCode = "original_binding_revoked", FailureOutcome = ConnectLinkCreationFailureOutcome.NotCreated }; }
        catch (BindingNotFoundException) { return new() { FailureCode = "original_binding_missing", FailureOutcome = ConnectLinkCreationFailureOutcome.NotCreated }; }
        if (string.IsNullOrWhiteSpace(capability.AccessToken))
            return new() { FailureCode = "original_sender_authorization_unavailable", FailureOutcome = ConnectLinkCreationFailureOutcome.NotCreated };

        using var client = clientFactory.CreateClient();
        var request = registration.ConnectLinkRequest;
        var result = await client.CreateConnectLinkAsync(capability.AccessToken, JsonSerializer.Serialize(new
        {
            service_slug = registration.RequestedCatalogServiceSlug,
            label = Optional(request.Label), requested_by = Optional(request.RequestedBy),
            callback_url = ResolveCallbackUrl(),
            expires_in = Math.Clamp(request.ExpiresInSeconds, 60, 3600),
        }, JsonOptions), ct).ConfigureAwait(false);
        if (!TryReadCreatedLink(result, out var id, out var url, out var expiry) || expiry <= timeProvider.GetUtcNow())
            return new() { FailureCode = "invalid_nyxid_connect_link_response", FailureOutcome = ConnectLinkCreationFailureOutcome.Uncertain };

        return new ConnectLinkCreationResult
        {
            ExternalRequestId = id!, ConnectUrl = url!,
            ExpiresAtUnixMs = Math.Min(registration.ExpiresAtUnixMs, expiry.ToUnixTimeMilliseconds()),
        };
    }

    internal static string ResolveCallbackUrl()
    {
        var oauthRedirect = NyxIdRedirectUriResolver.Resolve();
        return oauthRedirect[..^NyxIdRedirectUriResolver.CallbackPath.Length] + ConnectLinkCallbackEndpoints.CallbackPath;
    }

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryReadCreatedLink(string json, out string? id, out string? url, out DateTimeOffset expiry)
    {
        id = null; url = null; expiry = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idValue) ||
                idValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id = idValue.GetString()) ||
                !root.TryGetProperty("connect_url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url = urlValue.GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !root.TryGetProperty("expires_at", out var expiresValue) || expiresValue.ValueKind != JsonValueKind.String)
                return false;
            return DateTimeOffset.TryParse(expiresValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out expiry);
        }
        catch (JsonException) { return false; }
    }
}
