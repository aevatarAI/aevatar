using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Device;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Tests for DeviceEventEndpoints internal helper methods.
/// Uses InternalsVisibleTo to call ParseCallbackPayload and VerifyHmacSignature directly.
/// </summary>
public class DeviceEventEndpointsTests
{
    // ─── Parse Callback Payload Tests ───

    [Fact]
    public void DeviceEventEndpoints_ShouldNotOwnActorRuntimeDispatchOrEnvelopeConstruction()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../agents/Aevatar.GAgents.Device/DeviceEventEndpoints.cs"));
        var source = File.ReadAllText(sourcePath);

        source.Should().NotContain("IActorRuntime");
        source.Should().NotContain("IActorDispatchPort");
        source.Should().NotContain("new EventEnvelope");
        source.Should().NotContain("Any.Pack");
        source.Should().NotContain("EnvelopeRouteSemantics");
        source.Should().NotContain("CreateAsync<HouseholdEntity>");
    }

    [Fact]
    public void ParseCallbackPayload_known_temperature_maps_to_sensor_payload()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-001",
            source = "temperature-sensor",
            event_type = "temperature_change",
            timestamp = "2026-04-09T10:00:00Z",
            temperature = 28.5,
            humidity = 65.0,
            light_level = 70.0,
            motion = true,
        });

        var payload = EncodeCallbackPayload(innerEvent, senderPlatformId: "device-42");
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.Should().NotBeNull();
        inbound.EventId.Should().Be("evt-001");
        inbound.Source.Should().Be("temperature-sensor");
        inbound.EventType.Should().Be("temperature_change");
        inbound.Timestamp.Should().Be("2026-04-09T10:00:00Z");
        inbound.DeviceId.Should().Be("device-42");
        inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Sensor);
        inbound.Sensor.Temperature.Should().Be(28.5);
        inbound.Sensor.Humidity.Should().Be(65.0);
        inbound.Sensor.LightLevel.Should().Be(70.0);
        inbound.Sensor.MotionDetected.Should().BeTrue();
    }

    [Theory]
    [InlineData("temperature_change", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Sensor)]
    [InlineData("person_detected", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Camera)]
    [InlineData("scene_summary", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Camera)]
    [InlineData("motion_detected", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Motion)]
    [InlineData("speech_detected", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Speech)]
    public void ParseCallbackPayload_known_v1_event_types_map_to_typed_payload_cases(
        string eventType,
        Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase expectedPayloadCase)
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = $"evt-{eventType}",
            source = "device-adapter",
            event_type = eventType,
            temperature = 22.5,
            description = "Person near the front door",
            detected = true,
            text = "Lights on",
        });

        var payload = EncodeCallbackPayload(innerEvent, senderPlatformId: "device-v1");
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.EventType.Should().Be(eventType);
        inbound.PayloadCase.Should().Be(expectedPayloadCase);
    }

    [Fact]
    public void ParseCallbackPayload_legacy_sender_id_also_works()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-002",
            event_type = "motion_detected",
            detected = true,
        });

        var payload = EncodeCallbackPayload(innerEvent, legacySenderId: "legacy-device-1");
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.DeviceId.Should().Be("legacy-device-1");
        inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Motion);
        inbound.Motion.Detected.Should().BeTrue();
    }

    [Fact]
    public void ParseCallbackPayload_motion_detected_explicit_false_maps_to_false()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-003",
            event_type = "motion_detected",
            motion = false,
        });

        var payload = JsonSerializer.Serialize(new
        {
            content = new { text = innerEvent },
            sender = new { id = "device-3" },
        });

        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Motion);
        inbound.Motion.Detected.Should().BeFalse();
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
            sender = new { platform_id = "device-1" },
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
            sender = new { platform_id = "device-1" },
        });

        var bodyBytes = Encoding.UTF8.GetBytes(payload);

        var act = () => DeviceEventEndpoints.ParseCallbackPayload(bodyBytes);

        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("person_detected")]
    [InlineData("scene_summary")]
    [InlineData("camera_scene")]
    public void ParseCallbackPayload_known_camera_maps_to_camera_payload(string eventType)
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = $"evt-{eventType}",
            source = "camera-analyzer",
            event_type = eventType,
            description = "Two people sitting in the living room",
        });

        var payload = EncodeCallbackPayload(innerEvent, legacySenderId: "device-7");
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.EventId.Should().Be($"evt-{eventType}");
        inbound.Source.Should().Be("camera-analyzer");
        inbound.EventType.Should().Be(eventType);
        inbound.DeviceId.Should().Be("device-7");
        inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Camera);
        inbound.Camera.SceneDescription.Should().Be("Two people sitting in the living room");
    }

    [Fact]
    public void ParseCallbackPayload_unknown_event_type_throws_without_raw_payload()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-unknown",
            event_type = "unknown_type",
            payload = new { foo = "bar" },
        });

        var payload = JsonSerializer.Serialize(new
        {
            content = new { text = innerEvent },
            sender = new { id = "device-7" },
        });

        var act = () => DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        act.Should().Throw<JsonException>()
            .WithMessage("*Unsupported device event_type*");
        Aevatar.GAgents.Household.DeviceInbound.Descriptor.FindFieldByName("payload_json").Should().BeNull();
    }

    [Fact]
    public void DeviceInbound_contract_should_not_expose_raw_payload_bag_fields()
    {
        var fieldNames = Aevatar.GAgents.Household.DeviceInbound.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .ToArray();

        fieldNames.Should().NotContain("payload_json");
        fieldNames.Should().NotContain("raw_payload");
        fieldNames.Should().NotContain("payload_bytes");
        fieldNames.Should().NotContain("metadata");
        Aevatar.GAgents.Household.DeviceInbound.Descriptor.Oneofs
            .Should().ContainSingle(oneof => oneof.Name == "payload");
    }

    [Fact]
    public async Task HandleDeviceCallbackAsync_known_event_type_dispatches_typed_inbound_without_raw_payload()
    {
        DeviceCallbackDispatchCommand? capturedCommand = null;
        var queryPort = Substitute.For<IDeviceRegistrationQueryPort>();
        var callbackService = Substitute.For<IDeviceCallbackCommandService>();
        queryPort.GetAsync("reg-known", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeviceRegistrationEntry?>(MakeRegistration()));
        callbackService.DispatchCallbackAsync(
                Arg.Do<DeviceCallbackDispatchCommand>(command => capturedCommand = command),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                CommandDispatchResult<DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>.Success(
                    new DeviceCommandAcceptedReceipt(
                        "household-reg-known",
                        "cmd-known",
                        "corr-known",
                        "reg-known"))));

        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-known",
            source = "temperature-sensor",
            event_type = "temperature_change",
            temperature = 23.5,
            humidity = 44.0,
            light_level = 18.0,
        });
        var context = CreateJsonHttpContext(EncodeCallbackPayload(innerEvent, senderPlatformId: "sensor-1"));

        var result = await InvokeHandleDeviceCallbackAsync(
            context,
            "reg-known",
            queryPort,
            callbackService);
        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status202Accepted);
        body.Should().Contain("accepted");
        capturedCommand.Should().NotBeNull();
        capturedCommand!.RegistrationId.Should().Be("reg-known");
        capturedCommand.Inbound.DeviceId.Should().Be("sensor-1");
        capturedCommand.Inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Sensor);
        capturedCommand.Inbound.Sensor.Temperature.Should().Be(23.5);
        Aevatar.GAgents.Household.DeviceInbound.Descriptor.FindFieldByName("payload_json").Should().BeNull();
        await callbackService.Received(1)
            .DispatchCallbackAsync(Arg.Any<DeviceCallbackDispatchCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDeviceCallbackAsync_unknown_event_type_returns_reject_status_and_does_not_dispatch()
    {
        var queryPort = Substitute.For<IDeviceRegistrationQueryPort>();
        var callbackService = Substitute.For<IDeviceCallbackCommandService>();
        queryPort.GetAsync("reg-unknown", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeviceRegistrationEntry?>(MakeRegistration()));

        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-unknown",
            event_type = "unknown_type",
            payload = new { raw = "not admitted" },
        });
        var context = CreateJsonHttpContext(EncodeCallbackPayload(innerEvent));

        var result = await InvokeHandleDeviceCallbackAsync(
            context,
            "reg-unknown",
            queryPort,
            callbackService);
        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("Invalid callback payload");
        await callbackService.DidNotReceiveWithAnyArgs()
            .DispatchCallbackAsync(default!, default);
    }

    [Fact]
    public void ParseCallbackPayload_inner_json_array_throws_before_dispatch_mapping()
    {
        var payload = JsonSerializer.Serialize(new
        {
            content = new { text = "[]" },
            sender = new { id = "device-7" },
        });

        var act = () => DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ParseCallbackPayload_known_speech_maps_to_speech_payload()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-speech",
            source = "microphone",
            event_type = "speech_detected",
            text = "Turn on the lights",
        });

        var payload = EncodeCallbackPayload(innerEvent, senderPlatformId: "microphone-1");
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Speech);
        inbound.Speech.Text.Should().Be("Turn on the lights");
    }

    [Fact]
    public void ParseCallbackPayload_motion_detected_returns_motion_payload()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-motion",
            event_type = "motion_detected",
            detected = false,
        });

        var payload = EncodeCallbackPayload(innerEvent, senderPlatformId: "motion-1");
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.Motion);
        inbound.Motion.Detected.Should().BeFalse();
    }

    [Fact]
    public void ParseCallbackPayload_malformed_typed_payload_throws_at_adapter_boundary()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-bad-sensor",
            event_type = "temperature_change",
            temperature = "hot",
        });

        var payload = EncodeCallbackPayload(innerEvent, senderPlatformId: "device-1");

        var act = () => DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void DeviceCallbackCommandFacade_ShouldPackDeviceInboundOnceInSingleEventEnvelopePayload()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../agents/Aevatar.GAgents.Device/DeviceCommandFacades.cs"));
        var source = File.ReadAllText(sourcePath);
        var callbackFactoryStart = source.IndexOf("internal sealed class DeviceCallbackCommandEnvelopeFactory", StringComparison.Ordinal);
        callbackFactoryStart.Should().BeGreaterThanOrEqualTo(0);
        var callbackReceiptFactoryStart = source.IndexOf("internal sealed class DeviceCallbackCommandReceiptFactory", callbackFactoryStart, StringComparison.Ordinal);
        callbackReceiptFactoryStart.Should().BeGreaterThan(callbackFactoryStart);
        var callbackFactorySource = source[callbackFactoryStart..callbackReceiptFactoryStart];

        callbackFactorySource.Should().Contain("new EventEnvelope");
        callbackFactorySource.Should().Contain("Payload = Any.Pack(command.Inbound)");
        callbackFactorySource.Should().NotContain("Payload = Any.Pack(command)");
        callbackFactorySource.Should().NotContain("new DeviceCallbackEnvelope");
        callbackFactorySource.Should().Contain("EnvelopeRouteSemantics.CreateDirect(PublisherActorId, context.TargetId)");
        callbackFactorySource.Should().Contain("Refactor (issue1485/first-slice): Old pattern:");
    }

    // ─── HMAC Verification Tests ───

    private static DeviceRegistrationEntry MakeRegistration(string hmacKey = "test-secret") => new()
    {
        Id = "reg-test-1",
        ScopeId = "scope-test",
        HmacKey = hmacKey,
        CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static string EncodeCallbackPayload(
        string innerEvent,
        string? senderPlatformId = null,
        string? legacySenderId = null)
    {
        object sender = legacySenderId is not null
            ? new { id = legacySenderId }
            : new { platform_id = senderPlatformId ?? "device-1", display_name = "sensor-hub" };

        return JsonSerializer.Serialize(new
        {
            message_id = "nxmsg-55",
            platform = "device",
            agent = new { api_key_id = "key-1", name = "home-agent" },
            conversation = new { id = "conv-99", platform_id = "conv-p-99", conversation_type = "direct" },
            sender,
            content = new { content_type = "text", text = innerEvent, attachments = Array.Empty<object>() },
            timestamp = "2026-04-09T10:00:00Z",
        });
    }

    private static HttpContext CreateJsonHttpContext(string body)
    {
        var builder = WebApplication.CreateBuilder();
        var context = new DefaultHttpContext();
        context.RequestServices = builder.Services.BuildServiceProvider();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<IResult> InvokeHandleDeviceCallbackAsync(
        HttpContext context,
        string registrationId,
        IDeviceRegistrationQueryPort queryPort,
        IDeviceCallbackCommandService callbackCommandService)
    {
        var method = typeof(DeviceEventEndpoints)
            .GetMethod("HandleDeviceCallbackAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleDeviceCallbackAsync was not found.");

        var invocationResult = method.Invoke(null, new object[]
        {
            context,
            registrationId,
            queryPort,
            callbackCommandService,
            Options.Create(new DeviceEventOptions { SkipHmacVerification = true }),
            LoggerFactory.Create(static _ => { }),
            CancellationToken.None,
        });

        if (invocationResult is Task<IResult> resultTask)
            return await resultTask;

        throw new InvalidOperationException("HandleDeviceCallbackAsync did not return Task<IResult>.");
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var builder = WebApplication.CreateBuilder();
        var context = new DefaultHttpContext();
        context.RequestServices = builder.Services.BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

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
