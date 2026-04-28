using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Narrow port that resolves the current NyxID user id from a session token.
/// Extracted from <c>AgentBuilderTool.ResolveCurrentUserIdAsync</c> so caller-scope
/// resolvers can depend on this surface instead of the heavier <see cref="NyxIdApiClient"/>.
/// Returns <c>null</c> on any error envelope / malformed payload — caller decides
/// whether to fail closed or try a different identity source.
/// </summary>
public interface INyxIdCurrentUserResolver
{
    Task<string?> ResolveCurrentUserIdAsync(string nyxIdAccessToken, CancellationToken ct = default);
}

/// <summary>
/// Default implementation: queries NyxID `/me` and reads the user id from the response.
/// Mirrors the existing parser logic in <c>AgentBuilderTool.ResolveCurrentUserIdAsync</c>
/// so behavior stays consistent across surfaces.
/// </summary>
public sealed class NyxIdCurrentUserResolver : INyxIdCurrentUserResolver
{
    private readonly NyxIdApiClient _client;

    public NyxIdCurrentUserResolver(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<string?> ResolveCurrentUserIdAsync(string nyxIdAccessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nyxIdAccessToken))
            return null;

        var response = await _client.GetCurrentUserAsync(nyxIdAccessToken, ct);
        if (IsErrorPayload(response))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("user", out var user))
                return ReadString(user, "id", "user_id", "sub");

            return ReadString(doc.RootElement, "id", "user_id", "sub");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsErrorPayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty("error", out var errorProp)
                   && errorProp.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property)) continue;
            if (property.ValueKind == JsonValueKind.String) return property.GetString();
            if (property.ValueKind == JsonValueKind.Number) return property.GetRawText();
        }
        return null;
    }
}
