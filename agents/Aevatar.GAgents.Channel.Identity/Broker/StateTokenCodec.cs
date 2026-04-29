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
/// — see ADR-0018 §Implementation Notes #1.
/// </summary>
public sealed class StateTokenCodec
{
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<NyxIdBrokerOptions> _optionsMonitor;
    private readonly TimeProvider _timeProvider;

    public StateTokenCodec(
        Microsoft.Extensions.Options.IOptionsMonitor<NyxIdBrokerOptions> optionsMonitor,
        TimeProvider? timeProvider = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Convenience overload for tests / scenarios that already have a snapshot.
    public StateTokenCodec(NyxIdBrokerOptions options, TimeProvider? timeProvider = null)
        : this(StaticOptionsMonitor.Of(options ?? throw new ArgumentNullException(nameof(options))), timeProvider)
    {
    }

    private NyxIdBrokerOptions _options => _optionsMonitor.CurrentValue;
    private byte[] HmacKeyBytes()
    {
        var key = _options.StateTokenHmacKey;
        return string.IsNullOrEmpty(key) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(key);
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
        // here so verification stays stable. See ADR-0018 §Implementation
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
        // during a grace window — see ADR-0018 §Implementation Notes #1.
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
        var keyBytes = HmacKeyBytes();
        if (keyBytes.Length == 0)
            throw new InvalidOperationException("NyxIdBrokerOptions.StateTokenHmacKey is not configured.");
        return HMACSHA256.HashData(keyBytes, data);
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

    // Adapter so the legacy snapshot-based constructor can wrap a fixed
    // NyxIdBrokerOptions into the IOptionsMonitor surface. Used by tests
    // and other callers that don't go through DI options.
    private sealed class StaticOptionsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<NyxIdBrokerOptions>
    {
        private readonly NyxIdBrokerOptions _value;
        public StaticOptionsMonitor(NyxIdBrokerOptions value) { _value = value; }
        public static Microsoft.Extensions.Options.IOptionsMonitor<NyxIdBrokerOptions> Of(NyxIdBrokerOptions v) =>
            new StaticOptionsMonitor(v);
        public NyxIdBrokerOptions CurrentValue => _value;
        public NyxIdBrokerOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<NyxIdBrokerOptions, string?> listener) => null;
    }
}
