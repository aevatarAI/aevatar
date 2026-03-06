namespace Aevatar.Workflow.Abstractions;

/// <summary>
/// Callback token issue request for bridge-driven external callbacks.
/// </summary>
public sealed record BridgeCallbackTokenIssueRequest
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string StepId { get; init; }
    public required string SignalName { get; init; }
    public required int TimeoutMs { get; init; }
    public string ChannelId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Callback token issue result.
/// </summary>
public sealed record BridgeCallbackTokenIssueResult
{
    public required string Token { get; init; }
    public required string TokenId { get; init; }
    public required BridgeCallbackTokenClaims Claims { get; init; }
}

/// <summary>
/// Parsed callback token claims.
/// </summary>
public sealed record BridgeCallbackTokenClaims
{
    public required string TokenId { get; init; }
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string StepId { get; init; }
    public required string SignalName { get; init; }
    public required long IssuedAtUnixTimeMs { get; init; }
    public required long ExpiresAtUnixTimeMs { get; init; }
    public required string Nonce { get; init; }
    public string ChannelId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Bridge callback token service abstraction.
/// </summary>
public interface IBridgeCallbackTokenService
{
    BridgeCallbackTokenIssueResult Issue(
        BridgeCallbackTokenIssueRequest request,
        DateTimeOffset nowUtc);

    bool TryValidate(
        string token,
        DateTimeOffset nowUtc,
        out BridgeCallbackTokenClaims claims,
        out string error);
}
