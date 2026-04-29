using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Encodes / verifies the OAuth <c>state</c> token used for binding flows.
/// The token is HMAC-SHA256 over a Protobuf-serialized
/// <see cref="StateTokenPayload"/> carrying the correlation id, the external
/// subject, the PKCE code verifier, and an absolute expiry (UNIX seconds).
/// Token shape: <c>base64url(kid) "." base64url(payload_proto) "." base64url(hmac)</c>.
/// HMAC is over the literal bytes <c>kid_bytes "." payload_proto_bytes</c>
/// — see ADR-0017 §Implementation Notes #1.
/// </summary>
/// <remarks>
/// Reads its HMAC key from <see cref="IAevatarOAuthClientProvider"/> (cluster-
/// singleton actor) so the key is provisioned automatically with the OAuth
/// client and rotates as a side effect of the actor's rotation command. No
/// appsettings / secrets-store dependency.
/// </remarks>
public sealed class StateTokenCodec
{
    private static readonly TimeSpan FallbackStateTokenLifetime = TimeSpan.FromMinutes(5);

    private readonly IAevatarOAuthClientProvider _clientProvider;
    private readonly TimeProvider _timeProvider;
    private readonly NyxIdBrokerOptions _options;

    public StateTokenCodec(
        IAevatarOAuthClientProvider clientProvider,
        IOptions<NyxIdBrokerOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _options = options?.Value ?? new NyxIdBrokerOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> EncodeAsync(string correlationId, ExternalSubjectRef externalSubject, string pkceVerifier, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pkceVerifier);
        ArgumentNullException.ThrowIfNull(externalSubject);

        var snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        if (snapshot.HmacKey.Length == 0)
            throw new InvalidOperationException("Aevatar OAuth client HMAC key is not provisioned.");

        var lifetime = ResolveStateTokenLifetime();
        var expiresAt = _timeProvider.GetUtcNow().Add(lifetime);
        var payload = new StateTokenPayload
        {
            CorrelationId = correlationId,
            ExternalSubject = externalSubject.Clone(),
            PkceVerifier = pkceVerifier,
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
        };

        // Encode with the snapshot's current kid (defaults to v1 on first
        // provision). Verifiers below may also accept the previous kid
        // during the rotation grace window.
        var encodingKid = string.IsNullOrEmpty(snapshot.HmacKid)
            ? AevatarOAuthClientGAgent.InitialHmacKid
            : snapshot.HmacKid;
        var kidBytes = Encoding.UTF8.GetBytes(encodingKid);
        var payloadBytes = SerializeDeterministically(payload);
        var signed = Combine(kidBytes, payloadBytes);
        var hmac = HMACSHA256.HashData(snapshot.HmacKey, signed);

        return $"{Base64UrlEncode(kidBytes)}.{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(hmac)}";
    }

    public async Task<DecodeResult> TryDecodeAsync(string stateToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stateToken))
            return DecodeResult.Failed("state_missing");

        var parts = stateToken.Split('.');
        if (parts.Length != 3)
            return DecodeResult.Failed("state_malformed");

        byte[] kidBytes;
        byte[] payloadBytes;
        byte[] hmacBytes;
        try
        {
            kidBytes = Base64UrlDecode(parts[0]);
            payloadBytes = Base64UrlDecode(parts[1]);
            hmacBytes = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return DecodeResult.Failed("state_malformed");
        }

        AevatarOAuthClientSnapshot snapshot;
        try
        {
            snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            return DecodeResult.Failed("state_signature_invalid");
        }
        if (snapshot.HmacKey.Length == 0)
            return DecodeResult.Failed("state_signature_invalid");

        var presentedKid = Encoding.UTF8.GetString(kidBytes);
        var keyToVerify = ResolveVerificationKey(snapshot, presentedKid);
        if (keyToVerify is null)
            return DecodeResult.Failed("state_kid_unknown");

        var expectedHmac = HMACSHA256.HashData(keyToVerify, Combine(kidBytes, payloadBytes));
        if (!CryptographicOperations.FixedTimeEquals(expectedHmac, hmacBytes))
            return DecodeResult.Failed("state_signature_invalid");

        StateTokenPayload parsed;
        try
        {
            parsed = StateTokenPayload.Parser.ParseFrom(payloadBytes);
        }
        catch (InvalidProtocolBufferException)
        {
            return DecodeResult.Failed("state_payload_invalid");
        }

        if (parsed.ExpiresAt is null || parsed.ExpiresAt.ToDateTimeOffset() < _timeProvider.GetUtcNow())
            return DecodeResult.Failed("state_expired");

        return DecodeResult.Ok(parsed);
    }

    private TimeSpan ResolveStateTokenLifetime() =>
        _options.StateTokenLifetime > TimeSpan.Zero ? _options.StateTokenLifetime : FallbackStateTokenLifetime;

    /// <summary>
    /// Pick the verification key for <paramref name="presentedKid"/>:
    ///   - matches snapshot's current kid → current key
    ///   - matches snapshot's previous kid AND demoted_at + lifetime is still
    ///     in the future → previous key (rotation grace window)
    ///   - otherwise: null (unknown / expired kid → caller returns
    ///     <c>state_kid_unknown</c>)
    /// </summary>
    private byte[]? ResolveVerificationKey(AevatarOAuthClientSnapshot snapshot, string presentedKid)
    {
        var currentKid = string.IsNullOrEmpty(snapshot.HmacKid)
            ? AevatarOAuthClientGAgent.InitialHmacKid
            : snapshot.HmacKid;
        if (string.Equals(presentedKid, currentKid, StringComparison.Ordinal))
            return snapshot.HmacKey;

        if (snapshot.PreviousHmacKey is { Length: > 0 }
            && !string.IsNullOrEmpty(snapshot.PreviousHmacKid)
            && string.Equals(presentedKid, snapshot.PreviousHmacKid, StringComparison.Ordinal))
        {
            // Grace window expires once the longest-lived in-flight token
            // signed with the previous key would itself have expired.
            var demotedAt = snapshot.PreviousHmacDemotedAt ?? DateTimeOffset.MinValue;
            var graceUntil = demotedAt + ResolveStateTokenLifetime();
            if (_timeProvider.GetUtcNow() <= graceUntil)
                return snapshot.PreviousHmacKey;
        }

        return null;
    }

    private static byte[] SerializeDeterministically(StateTokenPayload payload)
    {
        using var ms = new MemoryStream();
        using (var output = new CodedOutputStream(ms, leaveOpen: true))
        {
            payload.WriteTo(output);
        }
        return ms.ToArray();
    }

    private static byte[] Combine(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var combined = new byte[a.Length + 1 + b.Length];
        a.CopyTo(combined.AsSpan(0));
        combined[a.Length] = (byte)'.';
        b.CopyTo(combined.AsSpan(a.Length + 1));
        return combined;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    public sealed record DecodeResult(bool Succeeded, StateTokenPayload? Payload, string? ErrorCode)
    {
        public static DecodeResult Ok(StateTokenPayload payload) => new(true, payload, null);
        public static DecodeResult Failed(string errorCode) => new(false, null, errorCode);
    }
}
