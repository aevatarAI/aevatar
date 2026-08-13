using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class WorkflowWebhookIngressAuthenticator
{
    public static WorkflowWebhookAuthenticationResult Authenticate(
        HttpRequest request,
        WorkflowWebhookIngressBindingOptions binding,
        ReadOnlySpan<byte> rawBody,
        DateTimeOffset receivedAt)
    {
        var secret = Normalize(binding.HmacSecret);
        if (secret == null)
            return WorkflowWebhookAuthenticationResult.Failure(
                "WEBHOOK_AUTH_CONFIG_REQUIRED",
                "Webhook HMAC secret is required.");

        var signatureHeader = Normalize(binding.HmacSignatureHeader) ?? "X-Aevatar-Signature";
        var timestampHeader = Normalize(binding.HmacTimestampHeader) ?? "X-Aevatar-Timestamp";
        var signature = Normalize(request.Headers[signatureHeader].FirstOrDefault());
        var timestampValue = Normalize(request.Headers[timestampHeader].FirstOrDefault());
        if (signature == null || timestampValue == null)
            return WorkflowWebhookAuthenticationResult.Failure(
                "WEBHOOK_AUTH_REQUIRED",
                "Webhook signature and timestamp are required.");

        if (!long.TryParse(timestampValue, out var timestampUnixSeconds))
            return WorkflowWebhookAuthenticationResult.Failure(
                "WEBHOOK_AUTH_INVALID",
                "Webhook timestamp is invalid.");

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(timestampUnixSeconds);
        var maxSkew = TimeSpan.FromSeconds(binding.MaxTimestampSkewSeconds <= 0
            ? 300
            : binding.MaxTimestampSkewSeconds);
        if (Duration(receivedAt - timestamp) > maxSkew)
            return WorkflowWebhookAuthenticationResult.Failure(
                "WEBHOOK_AUTH_EXPIRED",
                "Webhook timestamp is outside the accepted window.");

        // During secret rotation both the current and the retired secret
        // authenticate, so senders can be migrated without dropped deliveries.
        var matched = FixedTimeEquals(signature, ComputeSignature(secret, timestampValue, rawBody));
        var previousSecret = Normalize(binding.PreviousHmacSecret);
        if (!matched && previousSecret != null)
            matched = FixedTimeEquals(signature, ComputeSignature(previousSecret, timestampValue, rawBody));
        if (!matched)
            return WorkflowWebhookAuthenticationResult.Failure(
                "WEBHOOK_AUTH_INVALID",
                "Webhook signature is invalid.");

        return WorkflowWebhookAuthenticationResult.Success("hmac-sha256", Normalize(binding.SourceId) ?? string.Empty);
    }

    private static string ComputeSignature(
        string secret,
        string timestamp,
        ReadOnlySpan<byte> rawBody)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var payload = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(payload);
        rawBody.CopyTo(payload.AsSpan(prefix.Length));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var normalizedActual = StripPrefix(actual);
        var normalizedExpected = StripPrefix(expected);
        var actualBytes = Encoding.UTF8.GetBytes(normalizedActual);
        var expectedBytes = Encoding.UTF8.GetBytes(normalizedExpected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static string StripPrefix(string value) =>
        value.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? value["sha256=".Length..]
            : value;

    private static TimeSpan Duration(TimeSpan value) =>
        value < TimeSpan.Zero ? value.Negate() : value;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
