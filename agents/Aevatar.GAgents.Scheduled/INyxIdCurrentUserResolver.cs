using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.GAgents.Scheduled;

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

    /// <summary>
    /// Defense-in-depth error detection for NyxID `/me` responses. <c>NyxIdApiClient</c>
    /// synthesizes <c>{"error": true, "status": &lt;http&gt;, "body": "..."}</c> on HTTP
    /// failures, but downstream proxies / future error wrappers may use other shapes
    /// (string message, nested object, etc.). This treats *any* non-null / non-false
    /// <c>error</c> value as an error envelope, so a stricter wrapper change downstream
    /// doesn't silently degrade caller-scope resolution to "treat error as success and
    /// parse a missing user id" — which would surface as a misleading
    /// <c>CallerScopeUnavailableException("malformed payload")</c> instead of an honest
    /// "NyxID returned an error envelope".
    ///
    /// Parse failures fail closed (return <c>true</c>): a corrupt payload IS an error
    /// state, not a success state. Returning <c>false</c> here would let the caller try
    /// to extract an id from undefined ground.
    /// </summary>
    private static bool IsErrorPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!doc.RootElement.TryGetProperty("error", out var errorProp))
                return false;

            return errorProp.ValueKind switch
            {
                // Explicit absence: e.g. {"error": null} or {"error": false} → not an error
                JsonValueKind.Null => false,
                JsonValueKind.False => false,
                // Any other shape — boolean true, string message ("502 Bad Gateway"),
                // nested object ({"code": 401, ...}), array, number — is an error.
                _ => true,
            };
        }
        catch (JsonException)
        {
            return true;
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
