using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdChatDelegationCredentialLifecyclePort
{
    Task<NyxIdChatDelegationCredentialResolution> ResolveAsync(
        string delegationToken,
        CancellationToken ct);
}

public sealed record NyxIdChatDelegationCredentialResolution(
    bool Succeeded,
    string? AccessToken = null,
    bool Refreshed = false,
    string? Detail = null);

public sealed class NyxIdChatDelegationCredentialLifecyclePort
    : INyxIdChatDelegationCredentialLifecyclePort
{
    internal static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _timeProvider;
    private readonly INyxIdApiClientFactory? _clientFactory;

    public NyxIdChatDelegationCredentialLifecyclePort(
        TimeProvider timeProvider,
        INyxIdApiClientFactory? clientFactory = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _clientFactory = clientFactory;
    }

    public async Task<NyxIdChatDelegationCredentialResolution> ResolveAsync(
        string delegationToken,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var token = delegationToken?.Trim() ?? string.Empty;
        if (!NyxIdDelegationTokenClaims.IsDelegationToken(token) ||
            !NyxIdDelegationTokenClaims.TryReadExpiry(token, out var expiresAt))
        {
            return new NyxIdChatDelegationCredentialResolution(
                false,
                Detail: "invalid_delegation_token");
        }

        if (expiresAt - _timeProvider.GetUtcNow() > RefreshWindow)
            return new NyxIdChatDelegationCredentialResolution(true, token);

        if (_clientFactory is null)
        {
            return new NyxIdChatDelegationCredentialResolution(
                false,
                Detail: "delegation_refresh_unavailable");
        }

        try
        {
            using var client = _clientFactory.CreateClient();
            using var timeout = new CancellationTokenSource(RefreshTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var refresh = await client.RefreshDelegationAsync(token, linked.Token).ConfigureAwait(false);
            if (!refresh.Succeeded || string.IsNullOrWhiteSpace(refresh.AccessToken))
            {
                return new NyxIdChatDelegationCredentialResolution(
                    false,
                    Detail: refresh.Detail ?? "delegation_refresh_failed");
            }

            var refreshedToken = refresh.AccessToken.Trim();
            if (!NyxIdDelegationTokenClaims.IsDelegationToken(refreshedToken) ||
                !NyxIdDelegationTokenClaims.TryReadExpiry(refreshedToken, out var refreshedExpiry) ||
                refreshedExpiry - _timeProvider.GetUtcNow() <= RefreshWindow)
            {
                return new NyxIdChatDelegationCredentialResolution(
                    false,
                    Detail: "invalid_refreshed_delegation_token");
            }

            return new NyxIdChatDelegationCredentialResolution(
                true,
                refreshedToken,
                Refreshed: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new NyxIdChatDelegationCredentialResolution(
                false,
                Detail: "delegation_refresh_timeout");
        }
        catch
        {
            return new NyxIdChatDelegationCredentialResolution(
                false,
                Detail: "delegation_refresh_failed");
        }
    }

}

internal static class NyxIdDelegationTokenClaims
{
    private const int MaxJwtPayloadSegmentLength = 16 * 1024;

    internal static bool IsDelegationToken(string token) =>
        TryReadPayload(token, static root =>
            root.TryGetProperty("delegated", out var delegated) &&
            delegated.ValueKind == JsonValueKind.True &&
            root.TryGetProperty("act", out var actor) &&
            actor.ValueKind == JsonValueKind.Object &&
            actor.TryGetProperty("sub", out var actorSubject) &&
            actorSubject.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(actorSubject.GetString()));

    internal static bool TryReadExpiry(string token, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        long expirySeconds = 0;
        var matched = TryReadPayload(token, root =>
            root.TryGetProperty("exp", out var expiryProperty) &&
            expiryProperty.ValueKind == JsonValueKind.Number &&
            expiryProperty.TryGetInt64(out expirySeconds));
        if (!matched)
            return false;

        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryReadPayload(string token, Func<JsonElement, bool> inspect)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        var segments = token.Split('.');
        if (segments.Length != 3 ||
            string.IsNullOrWhiteSpace(segments[1]) ||
            segments[1].Length > MaxJwtPayloadSegmentLength)
        {
            return false;
        }

        try
        {
            var payload = segments[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   inspect(document.RootElement);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }
}
