using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

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
public sealed class StateTokenCodec
{
    private readonly NyxIdBrokerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _hmacKeyBytes;

    public StateTokenCodec(NyxIdBrokerOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Cache the HMAC key bytes — StateTokenCodec is registered as a
        // singleton, so re-encoding the key on every sign call is wasted work
        // (mimo-v2.5-pro L145).
        _hmacKeyBytes = string.IsNullOrEmpty(_options.StateTokenHmacKey)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(_options.StateTokenHmacKey);
    }

    public string Encode(string correlationId, ExternalSubjectRef externalSubject, string pkceVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pkceVerifier);
        ArgumentNullException.ThrowIfNull(externalSubject);

        var expiresAt = _timeProvider.GetUtcNow().Add(_options.StateTokenLifetime);
        var payload = new StateTokenPayload
        {
            CorrelationId = correlationId,
            ExternalSubject = externalSubject.Clone(),
            PkceVerifier = pkceVerifier,
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
        };

        var kidBytes = Encoding.UTF8.GetBytes(_options.StateTokenKid);
        // Deterministic Protobuf serialization for the HMAC payload —
        // standard ToByteArray() is not guaranteed deterministic across
        // schema evolutions (e.g. future map<…> fields). Pin the invariant
        // here so verification stays stable. See ADR-0017 §Implementation
        // Notes #1 (deepseek-v4-pro L39).
        var payloadBytes = SerializeDeterministically(payload);
        var signed = Combine(kidBytes, payloadBytes);
        var hmac = HmacSign(signed);

        return $"{Base64UrlEncode(kidBytes)}.{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(hmac)}";
    }

    public bool TryDecode(string stateToken, out StateTokenPayload? payload, out string? errorCode)
    {
        payload = null;
        errorCode = null;

        if (string.IsNullOrWhiteSpace(stateToken))
        {
            errorCode = "state_missing";
            return false;
        }

        var parts = stateToken.Split('.');
        if (parts.Length != 3)
        {
            errorCode = "state_malformed";
            return false;
        }

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
            errorCode = "state_malformed";
            return false;
        }

        // Verify the kid matches an accepted version. Today we only accept the
        // current key; rotation can extend this to also accept the previous kid
        // during a grace window — see ADR-0017 §Implementation Notes #1.
        var presentedKid = Encoding.UTF8.GetString(kidBytes);
        if (!string.Equals(presentedKid, _options.StateTokenKid, StringComparison.Ordinal))
        {
            errorCode = "state_kid_unknown";
            return false;
        }

        var expectedHmac = HmacSign(Combine(kidBytes, payloadBytes));
        if (!CryptographicOperations.FixedTimeEquals(expectedHmac, hmacBytes))
        {
            errorCode = "state_signature_invalid";
            return false;
        }

        StateTokenPayload parsed;
        try
        {
            parsed = StateTokenPayload.Parser.ParseFrom(payloadBytes);
        }
        catch (InvalidProtocolBufferException)
        {
            errorCode = "state_payload_invalid";
            return false;
        }

        if (parsed.ExpiresAt is null || parsed.ExpiresAt.ToDateTimeOffset() < _timeProvider.GetUtcNow())
        {
            errorCode = "state_expired";
            return false;
        }

        payload = parsed;
        return true;
    }

    private byte[] HmacSign(ReadOnlySpan<byte> data)
    {
        if (_hmacKeyBytes.Length == 0)
            throw new InvalidOperationException("NyxIdBrokerOptions.StateTokenHmacKey is not configured.");
        return HMACSHA256.HashData(_hmacKeyBytes, data);
    }

    private static byte[] SerializeDeterministically(StateTokenPayload payload)
    {
        using var ms = new MemoryStream();
        using (var output = new Google.Protobuf.CodedOutputStream(ms, leaveOpen: true))
        {
            // Note: Protobuf C# does not currently expose a deterministic
            // serialization toggle on CodedOutputStream. StateTokenPayload's
            // current schema has only scalar / message-typed singular fields,
            // which serialize in tag order; this pinned helper is the
            // single chokepoint where deterministic serialization should land
            // when the schema grows (e.g. map<…> fields), preserving HMAC
            // verification across versions.
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
}
