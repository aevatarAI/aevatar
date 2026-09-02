using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

// 06-20-observatory-admin-cross-scope (G6): NyxID-backed user directory. Resolves an email to candidate
//   users via the admin-gated /api/v1/admin/users?search=. Returns scopeId = NyxID user id per match.
public sealed class NyxIdPlatformUserDirectory : IPlatformUserDirectory
{
    private readonly INyxIdUserReadApi _userReadApi;
    private readonly ILogger<NyxIdPlatformUserDirectory> _logger;

    public NyxIdPlatformUserDirectory(
        INyxIdUserReadApi userReadApi,
        ILogger<NyxIdPlatformUserDirectory>? logger = null)
    {
        _userReadApi = userReadApi;
        _logger = logger ?? NullLogger<NyxIdPlatformUserDirectory>.Instance;
    }

    public async Task<IReadOnlyList<PlatformUserMatch>> SearchByEmailAsync(
        string bearerToken,
        string email,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken) || string.IsNullOrWhiteSpace(email))
            return [];

        string raw;
        try
        {
            raw = await _userReadApi.SearchAdminUsersAsync(bearerToken, email.Trim(), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID admin user search failed; returning no matches.");
            return [];
        }

        return ParseMatches(raw);
    }

    // Parses {"users":[{id,email,role,...}],...}; fail-closed to empty on error envelope or malformed shape.
    internal static IReadOnlyList<PlatformUserMatch> ParseMatches(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return [];

            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.True)
                return [];

            if (!root.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
                return [];

            var matches = new List<PlatformUserMatch>(users.GetArrayLength());
            foreach (var user in users.EnumerateArray())
            {
                if (user.ValueKind != JsonValueKind.Object)
                    continue;

                var id = GetString(user, "id");
                if (id.Length == 0)
                    continue;

                matches.Add(new PlatformUserMatch(id, GetString(user, "email"), GetString(user, "role")));
            }

            return matches;
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
}
