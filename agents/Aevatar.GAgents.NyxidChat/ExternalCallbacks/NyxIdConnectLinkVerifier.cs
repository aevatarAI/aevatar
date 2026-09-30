using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.NyxidChat.ExternalCallbacks;

/// <summary>NyxID JSON adapter for exact, original-user-authorized continuation evidence.</summary>
public sealed class NyxIdConnectLinkVerifier(
    INyxIdApiClientFactory clientFactory,
    INyxIdConnectedServiceCapabilityIssuer capabilityIssuer) : IConnectLinkVerificationPort
{
    private const long MaxLinkBytes = 64 * 1024;
    private const long MaxInventoryBytes = 2 * 1024 * 1024;

    public async Task<ConnectLinkVerificationResult> VerifyAsync(
        ExternalCallbackRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ct.ThrowIfCancellationRequested();
        if (!HasAuthorization(registration.Authorization))
            return Failed("original_authorization_missing");
        if (string.IsNullOrWhiteSpace(registration.ExternalRequestId))
            return Unavailable("external_request_not_recorded");
        if (registration.Kind != ExternalCallbackKind.ConnectLink ||
            string.IsNullOrWhiteSpace(registration.RequestedCatalogServiceSlug))
            return Failed("connect_link_registration_invalid");

        try
        {
            var authorization = registration.Authorization;
            var capability = await capabilityIssuer.IssueByBindingIdAsync(
                authorization.ExternalSubject, authorization.BindingId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(capability.AccessToken))
                return Unavailable("original_authorization_unavailable");

            using var client = clientFactory.CreateClient();
            var response = await client.GetConnectLinkBoundedAsync(capability.AccessToken,
                registration.ExternalRequestId, MaxLinkBytes, ct).ConfigureAwait(false);
            if (!response.Succeeded)
                return ProviderFailure(response);

            using var document = JsonDocument.Parse(response.Content);
            var link = document.RootElement;
            if (!Matches(link, "id", registration.ExternalRequestId) ||
                !Matches(link, "service_slug", registration.RequestedCatalogServiceSlug))
                return Failed("connect_link_identity_mismatch");

            switch (String(link, "status"))
            {
                case "pending": return Pending("connect_link_pending");
                case "cancelled": return new() { Disposition = CallbackVerificationDisposition.Cancelled };
                case "expired": return new() { Disposition = CallbackVerificationDisposition.Expired };
                case "completed": break;
                default: return Unavailable("connect_link_status_unavailable");
            }

            if (!link.TryGetProperty("connected_service", out var connected) ||
                String(connected, "id") is not { Length: > 0 } instanceId ||
                String(connected, "slug") is not { Length: > 0 } instanceSlug)
                return Unavailable("connected_service_not_visible");

            var inventory = await client.ListUserServicesBoundedAsync(
                capability.AccessToken, MaxInventoryBytes, ct).ConfigureAwait(false);
            if (!inventory.Succeeded)
                return ProviderFailure(inventory);
            using var inventoryDocument = JsonDocument.Parse(inventory.Content);
            var evidence = ValidateExactInstance(inventoryDocument.RootElement, instanceId, instanceSlug);
            if (evidence is not null)
                return evidence;

            return new ConnectLinkVerificationResult
            {
                Disposition = CallbackVerificationDisposition.Succeeded,
                References = new ExternalCallbackVerifiedReferences
                {
                    BindingId = authorization.BindingId,
                    OwnerScopeId = authorization.OwnerScopeId,
                    ConnectLinkId = registration.ExternalRequestId,
                    ConnectedServiceId = instanceId,
                    ConnectedServiceSlug = instanceSlug,
                    CatalogServiceSlug = registration.RequestedCatalogServiceSlug,
                },
            };
        }
        catch (BindingChangedException) { return Failed("original_binding_changed"); }
        catch (BindingRevokedException) { return Failed("original_binding_revoked"); }
        catch (BindingNotFoundException) { return Failed("original_binding_missing"); }
        catch (JsonException) { return Unavailable("verification_response_invalid"); }
        catch (HttpRequestException) { return Unavailable("verification_unavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unavailable("verification_timeout"); }
    }

    internal static bool HasAuthorization(CallbackAuthorizationReference? authorization) =>
        authorization?.ExternalSubject is { } subject &&
        !string.IsNullOrWhiteSpace(subject.Platform) &&
        !string.IsNullOrWhiteSpace(subject.ExternalUserId) &&
        !string.IsNullOrWhiteSpace(authorization.BindingId) &&
        !string.IsNullOrWhiteSpace(authorization.OwnerScopeId);

    private static ConnectLinkVerificationResult? ValidateExactInstance(JsonElement root, string id, string slug)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("services", out var services) ||
            services.ValueKind != JsonValueKind.Array)
            return Unavailable("service_inventory_unavailable");

        var matches = services.EnumerateArray().Where(service => Matches(service, "id", id)).ToArray();
        if (matches.Length == 0)
            return Unavailable("connected_service_not_visible");
        if (matches.Length != 1 || !Matches(matches[0], "slug", slug))
            return Failed("connected_service_identity_mismatch");
        var exact = matches[0];
        if (!exact.TryGetProperty("is_active", out var active) || active.ValueKind != JsonValueKind.True)
            return Failed("connected_service_inactive");

        // NyxID create currently always uses the authenticated creator as the owner.
        // Its ACL-protected exact link read plus a personal instance in that same user's
        // inventory proves ownership without inventing an owner field on link status.
        if (!exact.TryGetProperty("credential_source", out var source) || !Matches(source, "type", "personal"))
            return Failed("connected_service_owner_mismatch");
        return null;
    }

    private static ConnectLinkVerificationResult ProviderFailure(NyxIdProxyTextResponse response) =>
        response.HttpStatus is 401 or 403 or 404
            ? Failed("original_user_access_rejected")
            : Unavailable("verification_unavailable");
    private static ConnectLinkVerificationResult Pending(string code) => new()
        { Disposition = CallbackVerificationDisposition.Pending, FailureCode = code };
    private static ConnectLinkVerificationResult Unavailable(string code) => new()
        { Disposition = CallbackVerificationDisposition.Unavailable, FailureCode = code };
    private static ConnectLinkVerificationResult Failed(string code) => new()
        { Disposition = CallbackVerificationDisposition.Failed, FailureCode = code };
    private static bool Matches(JsonElement parent, string property, string expected) =>
        string.Equals(String(parent, property), expected, StringComparison.Ordinal);
    private static string? String(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
