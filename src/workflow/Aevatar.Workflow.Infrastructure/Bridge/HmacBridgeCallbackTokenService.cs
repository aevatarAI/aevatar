using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Bridge;

internal sealed class HmacBridgeCallbackTokenService : IBridgeCallbackTokenService
{
    private const string TokenPrefix = "v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IOptionsMonitor<WorkflowBridgeOptions> _options;

    public HmacBridgeCallbackTokenService(IOptionsMonitor<WorkflowBridgeOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public BridgeCallbackTokenIssueResult Issue(
        BridgeCallbackTokenIssueRequest request,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actorId = NormalizeRequiredToken(request.ActorId, nameof(request.ActorId));
        var runId = NormalizeRequiredToken(request.RunId, nameof(request.RunId));
        var stepId = NormalizeRequiredToken(request.StepId, nameof(request.StepId));
        var signalName = NormalizeRequiredToken(request.SignalName, nameof(request.SignalName)).ToLowerInvariant();
        if (request.TimeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.TimeoutMs), "TimeoutMs must be positive.");

        var options = _options.CurrentValue;
        var maxTtlMs = Math.Clamp(options.MaxTokenTtlMs, 1_000, 86_400_000);
        var ttlMs = Math.Clamp(request.TimeoutMs, 1_000, maxTtlMs);

        var nowMs = nowUtc.ToUnixTimeMilliseconds();
        var tokenId = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var claims = new BridgeCallbackTokenClaims
        {
            TokenId = tokenId,
            ActorId = actorId,
            RunId = runId,
            StepId = stepId,
            SignalName = signalName,
            IssuedAtUnixTimeMs = nowMs,
            ExpiresAtUnixTimeMs = nowMs + ttlMs,
            Nonce = nonce,
            ChannelId = NormalizeOptionalToken(request.ChannelId),
            SessionId = NormalizeOptionalToken(request.SessionId),
            Metadata = NormalizeMetadata(request.Metadata),
        };

        var payload = SerializePayload(claims);
        var payloadSegment = Base64UrlEncode(payload);
        var signatureSegment = Base64UrlEncode(Sign(payloadSegment, ResolveSigningKey(options)));
        var token = string.Concat(TokenPrefix, ".", payloadSegment, ".", signatureSegment);
        return new BridgeCallbackTokenIssueResult
        {
            Token = token,
            TokenId = tokenId,
            Claims = claims,
        };
    }

    public bool TryValidate(
        string token,
        DateTimeOffset nowUtc,
        out BridgeCallbackTokenClaims claims,
        out string error)
    {
        claims = default!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "token is required";
            return false;
        }

        var segments = token.Trim().Split('.');
        if (segments.Length != 3 || !string.Equals(segments[0], TokenPrefix, StringComparison.Ordinal))
        {
            error = "token format is invalid";
            return false;
        }

        var payloadSegment = segments[1];
        var signatureSegment = segments[2];
        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = Base64UrlDecode(payloadSegment);
            signatureBytes = Base64UrlDecode(signatureSegment);
        }
        catch
        {
            error = "token base64 payload is invalid";
            return false;
        }

        var expectedSignature = Sign(payloadSegment, ResolveSigningKey(_options.CurrentValue));
        if (!CryptographicOperations.FixedTimeEquals(signatureBytes, expectedSignature))
        {
            error = "token signature mismatch";
            return false;
        }

        BridgeTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BridgeTokenPayload>(payloadBytes, JsonOptions);
        }
        catch
        {
            error = "token payload json is invalid";
            return false;
        }

        if (payload == null)
        {
            error = "token payload is empty";
            return false;
        }

        var nowMs = nowUtc.ToUnixTimeMilliseconds();
        claims = new BridgeCallbackTokenClaims
        {
            TokenId = NormalizeRequiredPayload(payload.TokenId, "tokenId"),
            ActorId = NormalizeRequiredPayload(payload.ActorId, "actorId"),
            RunId = NormalizeRequiredPayload(payload.RunId, "runId"),
            StepId = NormalizeRequiredPayload(payload.StepId, "stepId"),
            SignalName = NormalizeRequiredPayload(payload.SignalName, "signalName").ToLowerInvariant(),
            IssuedAtUnixTimeMs = payload.IssuedAtUnixTimeMs,
            ExpiresAtUnixTimeMs = payload.ExpiresAtUnixTimeMs,
            Nonce = NormalizeRequiredPayload(payload.Nonce, "nonce"),
            ChannelId = NormalizeOptionalToken(payload.ChannelId),
            SessionId = NormalizeOptionalToken(payload.SessionId),
            Metadata = NormalizeMetadata(payload.Metadata),
        };

        if (claims.ExpiresAtUnixTimeMs <= claims.IssuedAtUnixTimeMs)
        {
            error = "token lifetime is invalid";
            claims = default!;
            return false;
        }

        if (nowMs > claims.ExpiresAtUnixTimeMs)
        {
            error = "token expired";
            return false;
        }

        return true;
    }

    private static byte[] SerializePayload(BridgeCallbackTokenClaims claims)
    {
        var payload = new BridgeTokenPayload
        {
            TokenId = claims.TokenId,
            ActorId = claims.ActorId,
            RunId = claims.RunId,
            StepId = claims.StepId,
            SignalName = claims.SignalName,
            IssuedAtUnixTimeMs = claims.IssuedAtUnixTimeMs,
            ExpiresAtUnixTimeMs = claims.ExpiresAtUnixTimeMs,
            Nonce = claims.Nonce,
            ChannelId = claims.ChannelId,
            SessionId = claims.SessionId,
            Metadata = claims.Metadata,
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }

    private static byte[] Sign(string payloadSegment, byte[] signingKey)
    {
        using var hmac = new HMACSHA256(signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadSegment));
    }

    private static byte[] ResolveSigningKey(WorkflowBridgeOptions options)
    {
        var key = NormalizeOptionalToken(options.TokenSigningKey);
        if (key.Length == 0)
            throw new InvalidOperationException("WorkflowBridge:TokenSigningKey is required.");

        return Encoding.UTF8.GetBytes(key);
    }

    private static string NormalizeRequiredToken(string? value, string paramName)
    {
        var normalized = NormalizeOptionalToken(value);
        if (normalized.Length == 0)
            throw new ArgumentException("Value is required.", paramName);
        return normalized;
    }

    private static string NormalizeRequiredPayload(string? value, string fieldName)
    {
        var normalized = NormalizeOptionalToken(value);
        if (normalized.Length == 0)
            throw new InvalidOperationException($"token payload field '{fieldName}' is required");
        return normalized;
    }

    private static string NormalizeOptionalToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var normalizedKey = NormalizeOptionalToken(key);
            var normalizedValue = NormalizeOptionalToken(value);
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;
            normalized[normalizedKey] = normalizedValue;
        }

        return normalized;
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var pad = normalized.Length % 4;
        if (pad > 0)
            normalized = normalized.PadRight(normalized.Length + (4 - pad), '=');
        return Convert.FromBase64String(normalized);
    }

    private sealed record BridgeTokenPayload
    {
        public string? TokenId { get; init; }
        public string? ActorId { get; init; }
        public string? RunId { get; init; }
        public string? StepId { get; init; }
        public string? SignalName { get; init; }
        public long IssuedAtUnixTimeMs { get; init; }
        public long ExpiresAtUnixTimeMs { get; init; }
        public string? Nonce { get; init; }
        public string? ChannelId { get; init; }
        public string? SessionId { get; init; }
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }
}
