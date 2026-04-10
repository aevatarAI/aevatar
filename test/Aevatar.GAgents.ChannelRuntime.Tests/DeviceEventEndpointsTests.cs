using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Tests for DeviceEventEndpoints internal helper methods.
/// Uses InternalsVisibleTo to call ParseCallbackPayload and VerifyHmacSignature directly.
/// </summary>
public class DeviceEventEndpointsTests
{
    // ─── Parse Callback Payload Tests ───

    [Fact]
    public void ParseCallbackPayload_valid_json_returns_device_inbound()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-001",
            source = "temperature-sensor",
            event_type = "temperature_change",
            timestamp = "2026-04-09T10:00:00Z",
        });

        var payload = JsonSerializer.Serialize(new
        {
            content = new { text = innerEvent },
            sender = new { id = "device-42", name = "sensor-hub" },
            conversation_id = "conv-99",
            message_id = "msg-55",
        });

        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(bodyBytes);

        inbound.Should().NotBeNull();
        inbound.EventId.Should().Be("evt-001");
        inbound.Source.Should().Be("temperature-sensor");
        inbound.EventType.Should().Be("temperature_change");
        inbound.Timestamp.Should().Be("2026-04-09T10:00:00Z");
        inbound.DeviceId.Should().Be("device-42");
        inbound.PayloadJson.Should().Be(innerEvent);
    }

    [Fact]
    public void ParseCallbackPayload_malformed_json_throws()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("not valid json {{{");

        var act = () => DeviceEventEndpoints.ParseCallbackPayload(bodyBytes);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ParseCallbackPayload_missing_content_text_throws()
    {
        var payload = JsonSerializer.Serialize(new
        {
            sender = new { id = "device-1" },
        });

        var bodyBytes = Encoding.UTF8.GetBytes(payload);

        var act = () => DeviceEventEndpoints.ParseCallbackPayload(bodyBytes);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void ParseCallbackPayload_empty_content_text_throws()
    {
        var payload = JsonSerializer.Serialize(new
        {
            content = new { text = "" },
            sender = new { id = "device-1" },
        });

        var bodyBytes = Encoding.UTF8.GetBytes(payload);

        var act = () => DeviceEventEndpoints.ParseCallbackPayload(bodyBytes);

        // Empty string is not valid JSON for inner parsing
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ParseCallbackPayload_with_partial_inner_event_uses_defaults()
    {
        // Inner event JSON that lacks some optional fields
        var innerEvent = JsonSerializer.Serialize(new { event_id = "evt-partial" });

        var payload = JsonSerializer.Serialize(new
        {
            content = new { text = innerEvent },
            sender = new { id = "device-7" },
        });

        var bodyBytes = Encoding.UTF8.GetBytes(payload);
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(bodyBytes);

        inbound.EventId.Should().Be("evt-partial");
        inbound.Source.Should().BeEmpty();
        inbound.EventType.Should().BeEmpty();
        inbound.Timestamp.Should().BeEmpty();
    }

    // ─── HMAC Verification Tests ───

    private static DeviceRegistrationEntry MakeRegistration(string hmacKey = "test-secret") => new()
    {
        Id = "reg-test-1",
        ScopeId = "scope-test",
        HmacKey = hmacKey,
        CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static (HttpContext context, byte[] bodyBytes) CreateContextWithSignature(
        string body, string hmacKey)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var keyBytes = Encoding.UTF8.GetBytes(hmacKey);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(bodyBytes);
        var signature = Convert.ToHexStringLower(hash);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-NyxID-Signature"] = signature;
        return (context, bodyBytes);
    }

    [Fact]
    public void HmacVerification_valid_signature_returns_true()
    {
        const string body = "{\"test\":\"data\"}";
        const string hmacKey = "my-secret-key";

        var (context, bodyBytes) = CreateContextWithSignature(body, hmacKey);
        var registration = MakeRegistration(hmacKey);
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = DeviceEventEndpoints.VerifyHmacSignature(context, bodyBytes, registration, options);

        result.Should().BeTrue();
    }

    [Fact]
    public void HmacVerification_invalid_signature_returns_false()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-NyxID-Signature"] = "deadbeef0000invalid";

        var registration = MakeRegistration("my-secret-key");
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = DeviceEventEndpoints.VerifyHmacSignature(context, bodyBytes, registration, options);

        result.Should().BeFalse();
    }

    [Fact]
    public void HmacVerification_missing_signature_header_returns_false()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        // No X-NyxID-Signature header

        var registration = MakeRegistration("my-secret-key");
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = DeviceEventEndpoints.VerifyHmacSignature(context, bodyBytes, registration, options);

        result.Should().BeFalse();
    }

    [Fact]
    public void HmacVerification_empty_hmac_key_returns_false()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-NyxID-Signature"] = "some-signature";

        var registration = MakeRegistration(hmacKey: "");
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = DeviceEventEndpoints.VerifyHmacSignature(context, bodyBytes, registration, options);

        result.Should().BeFalse();
    }

    [Fact]
    public void HmacVerification_skip_enabled_always_returns_true()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        // No signature header at all

        var registration = MakeRegistration("any-key");
        var options = new DeviceEventOptions { SkipHmacVerification = true };

        var result = DeviceEventEndpoints.VerifyHmacSignature(context, bodyBytes, registration, options);

        result.Should().BeTrue();
    }

    [Fact]
    public void HmacVerification_skip_enabled_ignores_wrong_signature()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-NyxID-Signature"] = "completely-wrong";

        var registration = MakeRegistration("secret");
        var options = new DeviceEventOptions { SkipHmacVerification = true };

        var result = DeviceEventEndpoints.VerifyHmacSignature(context, bodyBytes, registration, options);

        result.Should().BeTrue();
    }
}
