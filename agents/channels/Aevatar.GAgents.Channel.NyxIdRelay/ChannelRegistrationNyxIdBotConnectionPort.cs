using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public interface IChannelRegistrationBotConnectionPort
{
    Task<VerifiedChannelBotServiceConnection?> CreateLarkAsync(
        VerifiedChannelRegistrationServiceSelection selection,
        string registrationId, string providerSlug, NyxChannelLarkCredentials credentials, CancellationToken ct);

    Task DeleteOwnedAsync(string accessToken, VerifiedChannelBotServiceConnection connection, CancellationToken ct);
}

public sealed class VerifiedChannelBotServiceConnection
{
    // Only the nested adapter's completed verification path can mint cleanup authority.
    private VerifiedChannelBotServiceConnection(
        string userServiceId, string providerSlug, string registrationId,
        string appId, ChannelRegistrationKeyOwner owner)
    {
        UserServiceId = userServiceId;
        ProviderSlug = providerSlug;
        RegistrationId = registrationId;
        AppId = appId;
        Owner = owner;
    }

    public string UserServiceId { get; }
    public string ProviderSlug { get; }
    public string RegistrationId { get; }
    public string AppId { get; }
    public ChannelRegistrationKeyOwner Owner { get; }
    public bool CreatedExclusivelyForRegistration => true;

    /// <summary>
    /// Adapts NyxID's connection creation and exact inventory facts. Public inventory does not
    /// expose app credential identity, so existing connections cannot be safely reused here.
    /// </summary>
    public sealed class ChannelRegistrationNyxIdBotConnectionPort(
        NyxIdApiClient client,
        IChannelRegistrationNyxIdAuthorizationPort authorization,
        ILogger<ChannelRegistrationNyxIdBotConnectionPort> logger) : IChannelRegistrationBotConnectionPort
    {
        public async Task<VerifiedChannelBotServiceConnection?> CreateLarkAsync(
            VerifiedChannelRegistrationServiceSelection selection,
            string registrationId, string providerSlug, NyxChannelLarkCredentials credentials, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(registrationId) ||
                string.IsNullOrWhiteSpace(credentials.AppId) || string.IsNullOrWhiteSpace(credentials.AppSecret) ||
                !string.IsNullOrWhiteSpace(providerSlug) && providerSlug.Trim() != "api-lark-bot")
                return null;

            var appId = credentials.AppId.Trim();
            var label = $"Aevatar channel lark proxy {registrationId}: {appId}";
            var payload = new Dictionary<string, object>
            {
                // This is the configured Lark catalog, not a caller-selected service identity.
                ["service_slug"] = "api-lark-bot",
                ["credential"] = JsonSerializer.Serialize(new { app_id = appId, app_secret = credentials.AppSecret.Trim() }),
                ["label"] = label,
            };
            if (selection.TargetOrganizationId is not null)
                payload["target_org_id"] = selection.TargetOrganizationId;

            var response = await client.CreateServiceAsync(selection.AccessToken, JsonSerializer.Serialize(payload), ct);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1) ||
                root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) ||
                !ReadIdentity(root, "id", out var id) || !ReadIdentity(root, "slug", out var slug) ||
                slug.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
                selection.Inventory.Any(service => service.Id == id || service.Slug == slug))
                return null;

            NyxIdApiAccessResult<NyxIdUserServices> inventory;
            try
            {
                inventory = await authorization.ReadUserServicesAsync(selection.AccessToken, ct);
            }
            catch (Exception)
            {
                LogRetainedResource(id, registrationId, "inventory_read_failed");
                throw;
            }
            if (!inventory.Succeeded)
            {
                LogRetainedResource(id, registrationId, "inventory_unavailable");
                return null;
            }
            var exact = inventory.Value!.Services.Where(service => service.Id == id).ToArray();
            if (exact.Length != 1 || exact[0].Slug != slug || exact[0].Label != label || !exact[0].IsActive ||
                !MatchesOwner(exact[0].CredentialSource, selection.Owner) ||
                inventory.Value.Services.Any(service => service.Id != id && (service.Slug == slug || service.Label == label)))
            {
                LogRetainedResource(id, registrationId, "inventory_identity_mismatch");
                return null;
            }

            // Creation provenance binds this app and purpose. The exact fresh inventory row proves
            // owner/active/id/slug/label; only their conjunction grants compensation ownership.
            return new VerifiedChannelBotServiceConnection(id, slug, registrationId, appId, selection.Owner);
        }

        public async Task DeleteOwnedAsync(string accessToken, VerifiedChannelBotServiceConnection connection, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(connection);
            var response = await client.DeleteServiceAsync(accessToken, connection.UserServiceId, ct);
            // NyxID DELETE succeeds with HTTP 204 and an empty body. The client represents
            // unsuccessful HTTP responses as nonempty error envelopes, which remain checked.
            if (string.IsNullOrWhiteSpace(response))
                return;
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal)
                    .Any(group => group.Count() != 1) ||
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                throw new InvalidOperationException("channel_service_connection_unavailable");
        }

        private void LogRetainedResource(string userServiceId, string registrationId, string stage) =>
            logger.LogWarning(
                "Channel connection retained after verification failure: code={FailureCode}, userServiceId={UserServiceId}, registrationId={RegistrationId}, stage={Stage}",
                "channel_service_connection_verification_failed_resource_retained", userServiceId, registrationId, stage);

        private static bool ReadIdentity(JsonElement root, string name, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
                return false;
            value = property.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value) && value == value.Trim();
        }

        private static bool MatchesOwner(NyxIdUserServiceCredentialSource source, ChannelRegistrationKeyOwner owner) =>
            owner.Kind switch
            {
                ChannelRegistrationKeyOwnerKind.Personal => source.Kind == NyxIdUserServiceCredentialSourceKind.Personal,
                ChannelRegistrationKeyOwnerKind.Organization => source.Kind == NyxIdUserServiceCredentialSourceKind.Organization &&
                    source.OrganizationId == owner.Id && source.Allowed && source.OrganizationRole == NyxIdOrganizationRole.Admin,
                _ => false,
            };
    }
}
