using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity.Endpoints;

/// <summary>
/// Validates incoming NyxID broker-revocation webhooks (Continuous Access
/// Evaluation channel — ChronoAIProject/NyxID#549 V2-7). NyxID signs each
/// webhook body with HMAC-SHA256 using the cluster-shared HMAC key seeded by
/// the OAuth client provisioning actor (see <see cref="IAevatarOAuthClientProvider"/>);
/// the signature is carried in the <c>X-NyxID-Signature</c> header as
/// <c>sha256=&lt;hex&gt;</c>.
/// </summary>
public sealed class BrokerRevocationWebhookValidator
{
    public const string SignatureHeader = "X-NyxID-Signature";
    private static readonly TimeSpan FallbackStateTokenLifetime = TimeSpan.FromMinutes(5);

    private readonly IAevatarOAuthClientProvider _clientProvider;
    private readonly NyxIdBrokerOptions _options;
    private readonly TimeProvider _timeProvider;

    public BrokerRevocationWebhookValidator(
        IAevatarOAuthClientProvider clientProvider,
        IOptions<NyxIdBrokerOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _options = options?.Value ?? new NyxIdBrokerOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BrokerRevocationValidationResult> ValidateAsync(HttpContext http, byte[] body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(body);

        var presented = http.Request.Headers[SignatureHeader].ToString();
        if (string.IsNullOrWhiteSpace(presented))
            return BrokerRevocationValidationResult.Failed("signature_missing");

        AevatarOAuthClientSnapshot snapshot;
        try
        {
            snapshot = await _clientProvider.GetAsync(ct).ConfigureAwait(false);
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            return BrokerRevocationValidationResult.Failed("hmac_key_unprovisioned");
        }

        if (snapshot.HmacKey.Length == 0)
            return BrokerRevocationValidationResult.Failed("hmac_key_unprovisioned");

        const string prefix = "sha256=";
        if (!presented.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return BrokerRevocationValidationResult.Failed("signature_scheme_unsupported");

        byte[] presentedHmac;
        try
        {
            presentedHmac = Convert.FromHexString(presented[prefix.Length..]);
        }
        catch (FormatException)
        {
            return BrokerRevocationValidationResult.Failed("signature_malformed");
        }

        // Try the current key first. If it doesn't match AND a previous key
        // is still inside the rotation grace window, also try that — NyxID
        // could have signed this webhook before the rotation propagated to
        // their side, and dropping the signal would silently miss real
        // revocations (PR #521 review kimi MAJOR security; parity with
        // StateTokenCodec.ResolveVerificationKey).
        if (VerifySignature(snapshot.HmacKey, body, presentedHmac))
            return ParseNotification(body);

        if (TryGetGraceWindowKey(snapshot, out var previousKey)
            && VerifySignature(previousKey, body, presentedHmac))
        {
            return ParseNotification(body);
        }

        return BrokerRevocationValidationResult.Failed("signature_mismatch");
    }

    private static bool VerifySignature(byte[] key, byte[] body, byte[] presentedHmac)
    {
        var expectedHmac = HMACSHA256.HashData(key, body);
        return CryptographicOperations.FixedTimeEquals(expectedHmac, presentedHmac);
    }

    private bool TryGetGraceWindowKey(AevatarOAuthClientSnapshot snapshot, out byte[] previousKey)
    {
        previousKey = Array.Empty<byte>();
        if (snapshot.PreviousHmacKey is not { Length: > 0 } pk)
            return false;
        if (snapshot.PreviousHmacDemotedAt is not { } demotedAt)
            return false;

        var lifetime = _options.StateTokenLifetime > TimeSpan.Zero
            ? _options.StateTokenLifetime
            : FallbackStateTokenLifetime;
        if (_timeProvider.GetUtcNow() > demotedAt + lifetime)
            return false;

        previousKey = pk;
        return true;
    }

    private static BrokerRevocationValidationResult ParseNotification(byte[] body)
    {
        BrokerRevocationNotification? notification;
        try
        {
            notification = JsonSerializer.Deserialize<BrokerRevocationNotificationDto>(body)?.ToNotification();
        }
        catch (JsonException)
        {
            return BrokerRevocationValidationResult.Failed("body_invalid_json");
        }

        if (notification is null)
            return BrokerRevocationValidationResult.Failed("body_missing");

        return BrokerRevocationValidationResult.Ok(notification);
    }
}

public sealed record BrokerRevocationValidationResult(bool Succeeded, string? ErrorCode, BrokerRevocationNotification? Notification)
{
    public static BrokerRevocationValidationResult Ok(BrokerRevocationNotification notification) =>
        new(true, null, notification);

    public static BrokerRevocationValidationResult Failed(string errorCode) =>
        new(false, errorCode, null);
}

public sealed record BrokerRevocationNotification(
    string EventType,
    string? BindingId,
    string? Reason,
    ExternalSubjectRef? ExternalSubject);

internal sealed record BrokerRevocationNotificationDto
{
    public string? EventType { get; init; }
    public string? BindingId { get; init; }
    public string? Reason { get; init; }
    public string? Platform { get; init; }
    public string? Tenant { get; init; }
    public string? ExternalUserId { get; init; }

    public BrokerRevocationNotification ToNotification()
    {
        ExternalSubjectRef? subject = null;
        if (!string.IsNullOrWhiteSpace(Platform) && !string.IsNullOrWhiteSpace(ExternalUserId))
        {
            subject = new ExternalSubjectRef
            {
                Platform = Platform!,
                Tenant = Tenant ?? string.Empty,
                ExternalUserId = ExternalUserId!,
            };
        }
        return new BrokerRevocationNotification(
            EventType: EventType ?? string.Empty,
            BindingId: BindingId,
            Reason: Reason,
            ExternalSubject: subject);
    }
}
