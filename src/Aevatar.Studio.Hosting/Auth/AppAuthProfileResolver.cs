using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Studio.Hosting.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Hosting.Auth;

internal interface IAppAuthProfileResolver
{
    Task<AppAuthProfileResponse?> ResolveAsync(
        HttpContext http,
        AppAuthProfileResponse? claimsProfile,
        CancellationToken ct);
}

internal sealed class NyxIdAppAuthProfileResolver(
    INyxIdUserReadApi userReadApi,
    ILogger<NyxIdAppAuthProfileResolver> logger) : IAppAuthProfileResolver
{
    private readonly INyxIdUserReadApi _userReadApi = userReadApi;
    private readonly ILogger<NyxIdAppAuthProfileResolver> _logger = logger;

    public async Task<AppAuthProfileResponse?> ResolveAsync(
        HttpContext http,
        AppAuthProfileResponse? claimsProfile,
        CancellationToken ct)
    {
        var bearerToken = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearerToken))
            return claimsProfile;

        try
        {
            var raw = await _userReadApi.GetCurrentUserAsync(bearerToken, ct).ConfigureAwait(false);
            var providerProfile = ParseCurrentUser(raw);
            return Merge(providerProfile, claimsProfile);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID /users/me failed while resolving /api/auth/me profile.");
            return claimsProfile;
        }
    }

    internal static AppAuthProfileResponse? ParseCurrentUser(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
                return null;

            return new AppAuthProfileResponse(
                Subject: ReadFirstString(root, "id", "sub", "subject"),
                Name: ReadFirstString(root, "name", "display_name", "displayName", "preferred_username"),
                Email: ReadFirstString(root, "email"),
                EmailVerified: ReadBoolean(root, "email_verified", "emailVerified"),
                Picture: ReadFirstString(root, "picture", "avatar_url", "avatarUrl"),
                Roles: ReadStringArray(root, "roles", "role"),
                Groups: ReadStringArray(root, "groups", "group"));
        }
    }

    private static AppAuthProfileResponse? Merge(
        AppAuthProfileResponse? providerProfile,
        AppAuthProfileResponse? claimsProfile)
    {
        if (providerProfile is null)
            return claimsProfile;

        if (claimsProfile is null)
            return providerProfile;

        return new AppAuthProfileResponse(
            Subject: FirstNonEmpty(providerProfile.Subject, claimsProfile.Subject),
            Name: FirstNonEmpty(providerProfile.Name, claimsProfile.Name),
            Email: FirstNonEmpty(providerProfile.Email, claimsProfile.Email),
            EmailVerified: providerProfile.EmailVerified ?? claimsProfile.EmailVerified,
            Picture: FirstNonEmpty(providerProfile.Picture, claimsProfile.Picture),
            Roles: providerProfile.Roles.Count > 0 ? providerProfile.Roles : claimsProfile.Roles,
            Groups: providerProfile.Groups.Count > 0 ? providerProfile.Groups : claimsProfile.Groups);
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.FirstOrDefault()?.Trim();
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = header[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;

    private static string? ReadFirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private static bool? ReadBoolean(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
                continue;

            var values = value.ValueKind switch
            {
                JsonValueKind.Array => value.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString()?.Trim())
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => item!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                JsonValueKind.String => SplitStringValues(value.GetString()),
                _ => [],
            };

            if (values.Count > 0)
                return values;
        }

        return [];
    }

    private static IReadOnlyList<string> SplitStringValues(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
}
