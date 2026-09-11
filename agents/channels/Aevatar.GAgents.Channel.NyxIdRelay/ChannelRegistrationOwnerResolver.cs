using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record VerifiedChannelRegistrationOwner(
    string AuthenticatedActorId,
    ChannelRegistrationKeyOwner KeyOwner,
    string? TargetOrganizationId);

public sealed record ChannelRegistrationOwnerResolution(
    VerifiedChannelRegistrationOwner? Owner,
    string ErrorCode)
{
    public bool Succeeded => Owner is not null && string.IsNullOrEmpty(ErrorCode);
}

public interface IChannelRegistrationOwnerResolver
{
    Task<ChannelRegistrationOwnerResolution> ResolveAsync(
        string accessToken,
        string registrationOwnerScopeId,
        CancellationToken ct);
}

public sealed class ChannelRegistrationOwnerResolver(
    INyxIdCurrentUserResolver currentUser,
    NyxIdApiClient nyxClient) : IChannelRegistrationOwnerResolver
{
    public async Task<ChannelRegistrationOwnerResolution> ResolveAsync(
        string accessToken,
        string registrationOwnerScopeId,
        CancellationToken ct)
    {
        try
        {
            return await ResolveCoreAsync(accessToken, registrationOwnerScopeId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Forbidden();
        }
    }

    private async Task<ChannelRegistrationOwnerResolution> ResolveCoreAsync(
        string accessToken,
        string registrationOwnerScopeId,
        CancellationToken ct)
    {
        if (!IsCanonicalIdentifier(accessToken) || !IsCanonicalIdentifier(registrationOwnerScopeId))
            return Forbidden();

        var authenticatedActorId = await currentUser.ResolveCurrentUserIdAsync(accessToken, ct);
        if (!IsCanonicalIdentifier(authenticatedActorId))
            return Forbidden();
        var actorId = authenticatedActorId!;

        if (string.Equals(actorId, registrationOwnerScopeId, StringComparison.Ordinal))
        {
            return Resolved(
                actorId,
                ChannelRegistrationKeyOwnerKind.Personal,
                registrationOwnerScopeId,
                targetOrganizationId: null);
        }

        var response = await nyxClient.ListOrgsAsync(accessToken, ct);
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            return Forbidden();

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("orgs", out var organizations) ||
                organizations.ValueKind != JsonValueKind.Array)
            {
                return Forbidden();
            }

            foreach (var organization in organizations.EnumerateArray())
            {
                if (organization.ValueKind != JsonValueKind.Object ||
                    !TryReadCanonicalString(organization, "id", out var organizationId) ||
                    !TryReadCanonicalString(organization, "your_role", out var role))
                {
                    return Forbidden();
                }

                if (!string.Equals(organizationId, registrationOwnerScopeId, StringComparison.Ordinal))
                    continue;

                return string.Equals(role, "admin", StringComparison.Ordinal)
                    ? Resolved(
                        actorId,
                        ChannelRegistrationKeyOwnerKind.Organization,
                        organizationId,
                        organizationId)
                    : Forbidden();
            }

            return Forbidden();
        }
        catch (JsonException)
        {
            return Forbidden();
        }
    }

    private static bool TryReadCanonicalString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return IsCanonicalIdentifier(value);
    }

    private static bool IsCanonicalIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static ChannelRegistrationOwnerResolution Resolved(
        string authenticatedActorId,
        ChannelRegistrationKeyOwnerKind ownerKind,
        string ownerId,
        string? targetOrganizationId) =>
        new(
            new VerifiedChannelRegistrationOwner(
                authenticatedActorId,
                new ChannelRegistrationKeyOwner(ownerKind, ownerId),
                targetOrganizationId),
            string.Empty);

    private static ChannelRegistrationOwnerResolution Forbidden() =>
        new(null, "service_owner_forbidden");
}
