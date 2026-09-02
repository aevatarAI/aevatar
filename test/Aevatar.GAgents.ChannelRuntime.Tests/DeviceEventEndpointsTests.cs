using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
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
    [InlineData("doorbell_pressed", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert)]
    [InlineData("smoke_detected", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert)]
    [InlineData("water_leak_detected", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert)]
    [InlineData("carbon_monoxide_detected", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert)]
    [InlineData("glass_break_detected", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert)]
    [InlineData("lock_tampered", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert)]
    [InlineData("alarm_triggered", Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert)]
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
            .Returns(Task.FromResult<DeviceRegistrationEntry?>(MakeRegistration(registrationId: "reg-known")));
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
        var context = CreateJsonHttpContext(EncodeCallbackPayload(
            innerEvent,
            senderPlatformId: "sensor-1",
            callbackTimestamp: DateTimeOffset.UtcNow.ToString("O")));

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
        capturedCommand.Admission.Should().NotBeNull();
        capturedCommand.Admission!.RegistrationId.Should().Be("reg-known");
        capturedCommand.Admission.MessageId.Should().Be("nxmsg-55");
        capturedCommand.Admission.DeliveryId.Should().Be("evt-known");
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
            .Returns(Task.FromResult<DeviceRegistrationEntry?>(MakeRegistration(registrationId: "reg-unknown")));

        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-unknown",
            event_type = "unknown_type",
            payload = new { raw = "not admitted" },
        });
        var context = CreateJsonHttpContext(EncodeCallbackPayload(
            innerEvent,
            callbackTimestamp: DateTimeOffset.UtcNow.ToString("O")));

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
    public async Task HandleDeviceCallbackAsync_stale_signed_body_returns_reject_status_and_does_not_dispatch()
    {
        var queryPort = Substitute.For<IDeviceRegistrationQueryPort>();
        var callbackService = Substitute.For<IDeviceCallbackCommandService>();
        queryPort.GetAsync("reg-stale", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeviceRegistrationEntry?>(MakeRegistration(registrationId: "reg-stale")));
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-stale-dispatch",
            event_type = "motion_detected",
            detected = true,
        });
        var context = CreateJsonHttpContext(EncodeCallbackPayload(
            innerEvent,
            callbackTimestamp: "2026-04-09T10:00:00Z"));

        var result = await InvokeHandleDeviceCallbackAsync(
            context,
            "reg-stale",
            queryPort,
            callbackService);
        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain(((int)DeviceCallbackAdmissionError.StaleSignedBodyTimestamp).ToString());
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

    [Theory]
    [InlineData("doorbell_pressed", "", Aevatar.GAgents.Household.DeviceEventSeverity.Info)]
    [InlineData("smoke_detected", "", Aevatar.GAgents.Household.DeviceEventSeverity.Critical)]
    [InlineData("water_leak_detected", "warning", Aevatar.GAgents.Household.DeviceEventSeverity.Warning)]
    [InlineData("carbon_monoxide_detected", "", Aevatar.GAgents.Household.DeviceEventSeverity.Critical)]
    [InlineData("glass_break_detected", "", Aevatar.GAgents.Household.DeviceEventSeverity.Critical)]
    [InlineData("lock_tampered", "", Aevatar.GAgents.Household.DeviceEventSeverity.Critical)]
    [InlineData("alarm_triggered", "", Aevatar.GAgents.Household.DeviceEventSeverity.Critical)]
    public void ParseCallbackPayload_home_alert_events_map_to_typed_alert_payload(
        string eventType,
        string severity,
        Aevatar.GAgents.Household.DeviceEventSeverity expectedSeverity)
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = $"evt-{eventType}",
            source = "home-assistant",
            event_type = eventType,
            severity,
            area = "kitchen",
            entity_id = "binary_sensor.kitchen",
            correlation_key = "ha-alert-1",
            summary = "Kitchen alert",
        });

        var payload = EncodeCallbackPayload(innerEvent, senderPlatformId: "ha-bridge");
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert);
        inbound.HomeAlert.Severity.Should().Be(expectedSeverity);
        inbound.HomeAlert.Area.Should().Be("kitchen");
        inbound.HomeAlert.EntityId.Should().Be("binary_sensor.kitchen");
        inbound.HomeAlert.CorrelationKey.Should().Be("ha-alert-1");
        inbound.HomeAlert.Summary.Should().Be("Kitchen alert");
    }

    [Fact]
    public void ParseCallbackPayload_home_alert_events_use_typed_fallback_fields()
    {
        var innerEvent = JsonSerializer.Serialize(new
        {
            event_id = "evt-fallback",
            source = "home-assistant",
            event_type = "alarm_triggered",
            area = "garage",
            entity_id = "alarm.garage",
            description = "Garage alarm",
        });

        var payload = EncodeCallbackPayload(innerEvent, senderPlatformId: "ha-bridge");
        var inbound = DeviceEventEndpoints.ParseCallbackPayload(Encoding.UTF8.GetBytes(payload));

        inbound.PayloadCase.Should().Be(Aevatar.GAgents.Household.DeviceInbound.PayloadOneofCase.HomeAlert);
        inbound.HomeAlert.Severity.Should().Be(Aevatar.GAgents.Household.DeviceEventSeverity.Critical);
        inbound.HomeAlert.CorrelationKey.Should().Be("evt-fallback");
        inbound.HomeAlert.Summary.Should().Be("Garage alarm");
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

    private static DeviceRegistrationEntry MakeRegistration(
        string hmacKey = "test-secret",
        string registrationId = "reg-test-1") => new()
    {
        Id = registrationId,
        ScopeId = "scope-test",
        HmacKey = hmacKey,
        CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static string EncodeCallbackPayload(
        string innerEvent,
        string? senderPlatformId = null,
        string? legacySenderId = null,
        string? callbackTimestamp = null,
        string messageId = "nxmsg-55")
    {
        object sender = legacySenderId is not null
            ? new { id = legacySenderId }
            : new { platform_id = senderPlatformId ?? "device-1", display_name = "sensor-hub" };

        return JsonSerializer.Serialize(new
        {
            message_id = messageId,
            platform = "device",
            agent = new { api_key_id = "key-1", name = "home-agent" },
            conversation = new { id = "conv-99", platform_id = "conv-p-99", conversation_type = "direct" },
            sender,
            content = new { content_type = "text", text = innerEvent, attachments = Array.Empty<object>() },
            timestamp = callbackTimestamp ?? "2026-04-09T10:00:00Z",
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
        IDeviceCallbackCommandService callbackCommandService,
        ISecretVault? secretVault = null)
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
            secretVault ?? new InMemorySecretVault(),
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
        string body,
        string hmacKey,
        string? nyxTimestampHeader = null)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var keyBytes = Encoding.UTF8.GetBytes(hmacKey);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(DeviceEventEndpoints.BuildSignaturePayload(bodyBytes));
        var signature = Convert.ToHexStringLower(hash);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-NyxID-Signature"] = signature;
        if (nyxTimestampHeader is not null)
            context.Request.Headers["X-NyxID-Timestamp"] = nyxTimestampHeader;
        return (context, bodyBytes);
    }

    [Fact]
    public async Task HmacVerification_valid_signature_returns_true()
    {
        const string hmacKey = "my-secret-key";
        var body = EncodeCallbackPayload(JsonSerializer.Serialize(new
        {
            event_id = "evt-signature",
            event_type = "motion_detected",
            detected = true,
        }));

        var (context, bodyBytes) = CreateContextWithSignature(body, hmacKey);
        var registration = MakeRegistration(hmacKey);
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, new InMemorySecretVault(), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task AdmitCallback_rejects_signed_body_outside_freshness_window()
    {
        const string hmacKey = "my-secret-key";
        var now = DateTimeOffset.Parse("2026-04-09T10:00:20Z");
        var body = EncodeCallbackPayload(
            JsonSerializer.Serialize(new
            {
                event_id = "evt-stale",
                event_type = "motion_detected",
                detected = true,
            }),
            callbackTimestamp: "2026-04-09T10:00:00Z");
        var (context, bodyBytes) = CreateContextWithSignature(body, hmacKey);

        var result = await DeviceEventEndpoints.AdmitCallback(
            context,
            bodyBytes,
            MakeRegistration(hmacKey),
            new DeviceEventOptions { CallbackFreshnessWindow = TimeSpan.FromSeconds(10) },
            now,
            new InMemorySecretVault(),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(DeviceCallbackAdmissionError.StaleSignedBodyTimestamp);
    }

    [Fact]
    public async Task AdmitCallback_rejects_captured_signed_body_replayed_after_window()
    {
        const string hmacKey = "my-secret-key";
        var signedAt = DateTimeOffset.Parse("2026-04-09T10:00:00Z");
        var body = EncodeCallbackPayload(
            JsonSerializer.Serialize(new
            {
                event_id = "evt-replay",
                event_type = "motion_detected",
                detected = true,
            }),
            callbackTimestamp: "2026-04-09T10:00:00Z");
        var (context, bodyBytes) = CreateContextWithSignature(body, hmacKey);
        var options = new DeviceEventOptions { CallbackFreshnessWindow = TimeSpan.FromSeconds(10) };
        var vault = new InMemorySecretVault();

        var first = await DeviceEventEndpoints.AdmitCallback(
            context,
            bodyBytes,
            MakeRegistration(hmacKey),
            options,
            signedAt.AddSeconds(2),
            vault,
            CancellationToken.None);
        var replay = await DeviceEventEndpoints.AdmitCallback(
            context,
            bodyBytes,
            MakeRegistration(hmacKey),
            options,
            signedAt.AddSeconds(11),
            vault,
            CancellationToken.None);

        first.Succeeded.Should().BeTrue();
        replay.Succeeded.Should().BeFalse();
        replay.Error.Should().Be(DeviceCallbackAdmissionError.StaleSignedBodyTimestamp);
    }

    [Fact]
    public async Task AdmitCallback_uses_body_timestamp_not_X_NyxID_Timestamp_as_freshness_authority()
    {
        const string hmacKey = "my-secret-key";
        var body = EncodeCallbackPayload(
            JsonSerializer.Serialize(new
            {
                event_id = "evt-header",
                event_type = "motion_detected",
                detected = true,
            }),
            callbackTimestamp: "2026-04-09T10:00:00Z");
        var (context, bodyBytes) = CreateContextWithSignature(
            body,
            hmacKey,
            nyxTimestampHeader: "2026-04-09T10:00:20Z");

        var result = await DeviceEventEndpoints.AdmitCallback(
            context,
            bodyBytes,
            MakeRegistration(hmacKey),
            new DeviceEventOptions { CallbackFreshnessWindow = TimeSpan.FromSeconds(10) },
            DateTimeOffset.Parse("2026-04-09T10:00:20Z"),
            new InMemorySecretVault(),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(DeviceCallbackAdmissionError.StaleSignedBodyTimestamp);
    }

    [Fact]
    public async Task AdmitCallback_rejects_body_timestamp_tampering_without_resigning()
    {
        const string hmacKey = "my-secret-key";
        var originalBody = EncodeCallbackPayload(
            JsonSerializer.Serialize(new
            {
                event_id = "evt-tamper",
                event_type = "motion_detected",
                detected = true,
            }),
            callbackTimestamp: "2026-04-09T10:00:00Z");
        var (context, _) = CreateContextWithSignature(originalBody, hmacKey);
        var tamperedBody = originalBody.Replace(
            "2026-04-09T10:00:00Z",
            "2026-04-09T10:00:20Z",
            StringComparison.Ordinal);

        var result = await DeviceEventEndpoints.AdmitCallback(
            context,
            Encoding.UTF8.GetBytes(tamperedBody),
            MakeRegistration(hmacKey),
            new DeviceEventOptions { CallbackFreshnessWindow = TimeSpan.FromSeconds(10) },
            DateTimeOffset.Parse("2026-04-09T10:00:20Z"),
            new InMemorySecretVault(),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(DeviceCallbackAdmissionError.SignatureRejected);
    }

    [Fact]
    public async Task AdmitCallback_uses_inner_body_timestamp_when_outer_timestamp_is_absent()
    {
        const string hmacKey = "my-secret-key";
        var body = JsonSerializer.Serialize(new
        {
            message_id = "nxmsg-inner-ts",
            platform = "device",
            content = new
            {
                content_type = "text",
                text = JsonSerializer.Serialize(new
                {
                    event_id = "evt-inner-ts",
                    event_type = "motion_detected",
                    timestamp = "2026-04-09T10:00:00Z",
                    detected = true,
                }),
            },
        });
        var (context, bodyBytes) = CreateContextWithSignature(body, hmacKey);

        var result = await DeviceEventEndpoints.AdmitCallback(
            context,
            bodyBytes,
            MakeRegistration(hmacKey),
            new DeviceEventOptions { CallbackFreshnessWindow = TimeSpan.FromSeconds(10) },
            DateTimeOffset.Parse("2026-04-09T10:00:02Z"),
            new InMemorySecretVault(),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Admission!.SignedBodyTimestampValue.Should().Be("2026-04-09T10:00:00Z");
        result.Admission.DeliveryId.Should().Be("evt-inner-ts");
    }

    [Fact]
    public async Task HmacVerification_invalid_signature_returns_false()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-NyxID-Signature"] = "deadbeef0000invalid";

        var registration = MakeRegistration("my-secret-key");
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, new InMemorySecretVault(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HmacVerification_missing_signature_header_returns_false()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        // No X-NyxID-Signature header

        var registration = MakeRegistration("my-secret-key");
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, new InMemorySecretVault(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HmacVerification_empty_hmac_key_returns_false()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-NyxID-Signature"] = "some-signature";

        var registration = MakeRegistration(hmacKey: "");
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, new InMemorySecretVault(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HmacVerification_skip_enabled_always_returns_true()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        // No signature header at all

        var registration = MakeRegistration("any-key");
        var options = new DeviceEventOptions { SkipHmacVerification = true };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, new InMemorySecretVault(), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HmacVerification_skip_enabled_ignores_wrong_signature()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-NyxID-Signature"] = "completely-wrong";

        var registration = MakeRegistration("secret");
        var options = new DeviceEventOptions { SkipHmacVerification = true };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, new InMemorySecretVault(), CancellationToken.None);

        result.Should().BeTrue();
    }

    // ─── Secret-vault migration (dual-read) ───

    [Fact]
    public async Task HmacVerification_resolves_key_from_vault_reference()
    {
        const string hmacKey = "vault-backed-signing-key-01234567";
        const string scopeId = "scope-vault";
        const string conversationId = "conv-vault";
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(
            new StoreSecretRequest(
                CredentialSecretPurposes.DeviceHmacSigningKey,
                scopeId,
                conversationId,
                hmacKey,
                "device.register"),
            CancellationToken.None);

        var body = EncodeCallbackPayload(JsonSerializer.Serialize(new
        {
            event_id = "evt-vault",
            event_type = "motion_detected",
            detected = true,
        }));
        var (context, bodyBytes) = CreateContextWithSignature(body, hmacKey);

        // New-write shape: HmacKey empty, only the reference present.
        var registration = new DeviceRegistrationEntry
        {
            Id = "reg-vault",
            ScopeId = scopeId,
            NyxConversationId = conversationId,
            HmacKeyRef = stored.Reference,
            CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, vault, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HmacVerification_unresolvable_reference_fails_closed()
    {
        const string scopeId = "scope-missing";
        var vault = new InMemorySecretVault();
        var body = EncodeCallbackPayload(JsonSerializer.Serialize(new
        {
            event_id = "evt-missing",
            event_type = "motion_detected",
            detected = true,
        }));
        var (context, bodyBytes) = CreateContextWithSignature(body, "anything-goes-here-32-characters!");

        // Reference points at a secret the vault never stored → resolve returns not-found → fail closed.
        var registration = new DeviceRegistrationEntry
        {
            Id = "reg-missing",
            ScopeId = scopeId,
            HmacKeyRef = new Aevatar.Foundation.Abstractions.Credentials.SecretReference
            {
                Ref = "sec_0000000000000099",
                Purpose = CredentialSecretPurposes.DeviceHmacSigningKey,
                OwnerScopeKey = scopeId,
            },
            CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, vault, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HmacVerification_legacy_plaintext_key_without_reference_still_verifies()
    {
        const string hmacKey = "legacy-plaintext-signing-key-0123";
        var vault = new InMemorySecretVault();
        var body = EncodeCallbackPayload(JsonSerializer.Serialize(new
        {
            event_id = "evt-legacy",
            event_type = "motion_detected",
            detected = true,
        }));
        var (context, bodyBytes) = CreateContextWithSignature(body, hmacKey);

        // Legacy shape: plaintext HmacKey present, no reference. Dual-read falls back to it.
        var registration = MakeRegistration(hmacKey, registrationId: "reg-legacy");
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, vault, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HmacVerification_dangling_reference_does_not_fall_through_to_legacy_key()
    {
        // Branch-exclusivity guard: when a reference is present but unresolvable, the
        // verifier must fail closed — it must NOT silently fall back to a still-populated
        // legacy plaintext key. The body is signed with the legacy key on purpose, so a
        // fall-through regression would accept it (this test would then catch it).
        const string legacyKey = "legacy-key-must-not-be-used-01234";
        const string scopeId = "scope-danglingref";
        var vault = new InMemorySecretVault();
        var body = EncodeCallbackPayload(JsonSerializer.Serialize(new
        {
            event_id = "evt-dangling",
            event_type = "motion_detected",
            detected = true,
        }));
        var (context, bodyBytes) = CreateContextWithSignature(body, legacyKey);

        var registration = new DeviceRegistrationEntry
        {
            Id = "reg-dangling",
            ScopeId = scopeId,
            HmacKey = legacyKey, // populated legacy key that must be ignored once a ref is present
            HmacKeyRef = new Aevatar.Foundation.Abstractions.Credentials.SecretReference
            {
                Ref = "sec_0000000000000099", // never stored → unresolvable
                Purpose = CredentialSecretPurposes.DeviceHmacSigningKey,
                OwnerScopeKey = scopeId,
            },
            CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, vault, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HmacVerification_unauthorized_reference_does_not_fall_through_to_legacy_key()
    {
        // The reference resolves to a real vault secret, but stored under a DIFFERENT owner
        // scope/subject than the registration claims, so the vault's purpose+owner+subject
        // binding denies it. Verification must fail closed, not fall back to the legacy key.
        const string legacyKey = "legacy-key-must-not-be-used-56789";
        const string vaultKey = "vault-secret-under-other-scope-01";
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(
            new StoreSecretRequest(
                CredentialSecretPurposes.DeviceHmacSigningKey,
                "scope-owner-a",
                "conv-owner-a",
                vaultKey,
                "device.register"),
            CancellationToken.None);

        var body = EncodeCallbackPayload(JsonSerializer.Serialize(new
        {
            event_id = "evt-unauthorized",
            event_type = "motion_detected",
            detected = true,
        }));
        var (context, bodyBytes) = CreateContextWithSignature(body, legacyKey);

        // Registration claims scope-owner-b but points at scope-owner-a's secret → vault denies.
        var registration = new DeviceRegistrationEntry
        {
            Id = "reg-unauthorized",
            ScopeId = "scope-owner-b",
            HmacKey = legacyKey, // populated legacy key that must be ignored once a ref is present
            HmacKeyRef = stored.Reference,
            CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var result = await DeviceEventEndpoints.VerifyHmacSignature(
            context, bodyBytes, registration, options, vault, CancellationToken.None);

        result.Should().BeFalse();
    }

    // ─── Registration ingress: length gate + encrypt-on-ingress ───

    [Fact]
    public async Task HandleRegisterDeviceAsync_short_key_returns_bad_request()
    {
        var facade = DeviceRegistrationCommandFacadeStub.ThatFailsIfCalled();
        var vault = new InMemorySecretVault();
        var context = CreateJsonHttpContext(JsonSerializer.Serialize(new
        {
            scope_id = "scope-short",
            hmac_key = "too-short",
            device_event_target_actor_id = "household-scope-short",
        }));

        var result = await InvokeHandleRegisterDeviceAsync(
            context, facade, vault, new DeviceEventOptions { SkipHmacVerification = false });
        var (statusCode, body) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("at least 32");
    }

    [Fact]
    public async Task HandleRegisterDeviceAsync_stores_key_as_vault_reference_and_leaves_legacy_field_empty()
    {
        DeviceRegisterCommand? capturedCommand = null;
        var facade = DeviceRegistrationCommandFacadeStub.Capturing(command => capturedCommand = command);
        var vault = new InMemorySecretVault();
        const string hmacKey = "a-sufficiently-long-signing-key-01";
        var context = CreateJsonHttpContext(JsonSerializer.Serialize(new
        {
            scope_id = "scope-ingress",
            hmac_key = hmacKey,
            nyx_conversation_id = "conv-ingress",
            device_event_target_actor_id = "household-scope-ingress",
        }));

        var result = await InvokeHandleRegisterDeviceAsync(
            context, facade, vault, new DeviceEventOptions { SkipHmacVerification = false });
        var (statusCode, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status202Accepted);
        capturedCommand.Should().NotBeNull();
        capturedCommand!.HmacKey.Should().BeEmpty();
        capturedCommand.HmacKeyRef.Should().NotBeNull();
        capturedCommand.HmacKeyRef!.Ref.Should().NotBeNullOrEmpty();
        capturedCommand.HmacKeyRef.Purpose.Should().Be(CredentialSecretPurposes.DeviceHmacSigningKey);

        // The reference resolves the original plaintext back through the vault
        // using the same subject helper (conversation id present → conversation id).
        var resolved = await vault.ResolveAsync(
            new ResolveSecretRequest(
                capturedCommand.HmacKeyRef.Ref,
                CredentialSecretPurposes.DeviceHmacSigningKey,
                "scope-ingress",
                "conv-ingress",
                "device.verify-callback"),
            CancellationToken.None);
        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be(hmacKey);
    }

    // ─── Production fail-fast gate ───

    [Fact]
    public void EnsureNotSkippingHmacInProduction_throws_when_skip_enabled_in_production()
    {
        var options = new DeviceEventOptions { SkipHmacVerification = true };

        var act = () => options.EnsureNotSkippingHmacInProduction(isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SkipHmacVerification*");
    }

    [Fact]
    public void EnsureNotSkippingHmacInProduction_allows_skip_outside_production()
    {
        var options = new DeviceEventOptions { SkipHmacVerification = true };

        var act = () => options.EnsureNotSkippingHmacInProduction(isProduction: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureNotSkippingHmacInProduction_allows_verification_enabled_in_production()
    {
        var options = new DeviceEventOptions { SkipHmacVerification = false };

        var act = () => options.EnsureNotSkippingHmacInProduction(isProduction: true);

        act.Should().NotThrow();
    }

    private static async Task<IResult> InvokeHandleRegisterDeviceAsync(
        HttpContext context,
        DeviceRegistrationCommandFacade facade,
        ISecretVault secretVault,
        DeviceEventOptions options)
    {
        var method = typeof(DeviceEventEndpoints)
            .GetMethod("HandleRegisterDeviceAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleRegisterDeviceAsync was not found.");

        var invocationResult = method.Invoke(null, new object[]
        {
            context,
            facade,
            secretVault,
            Options.Create(options),
            LoggerFactory.Create(static _ => { }),
            CancellationToken.None,
        });

        if (invocationResult is Task<IResult> resultTask)
            return await resultTask;

        throw new InvalidOperationException("HandleRegisterDeviceAsync did not return Task<IResult>.");
    }
}

/// <summary>
/// Test double for <see cref="DeviceRegistrationCommandFacade"/> that captures the dispatched
/// <see cref="DeviceRegisterCommand"/> without exercising the actor runtime.
/// </summary>
file static class DeviceRegistrationCommandFacadeStub
{
    public static DeviceRegistrationCommandFacade Capturing(Action<DeviceRegisterCommand> onRegister)
    {
        var registerDispatch = Substitute.For<ICommandDispatchService<DeviceRegisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>();
        registerDispatch.DispatchAsync(Arg.Do<DeviceRegisterCommand>(onRegister), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                CommandDispatchResult<DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>.Success(
                    new DeviceCommandAcceptedReceipt("device-registration-store", "cmd-x", "corr-x"))));
        var unregisterDispatch = Substitute.For<ICommandDispatchService<DeviceUnregisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>();
        return new DeviceRegistrationCommandFacade(registerDispatch, unregisterDispatch);
    }

    public static DeviceRegistrationCommandFacade ThatFailsIfCalled()
    {
        var registerDispatch = Substitute.For<ICommandDispatchService<DeviceRegisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>();
        registerDispatch.DispatchAsync(Arg.Any<DeviceRegisterCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommandDispatchResult<DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>>(
                _ => throw new InvalidOperationException("Registration dispatch must not be reached for a rejected request."));
        var unregisterDispatch = Substitute.For<ICommandDispatchService<DeviceUnregisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>>();
        return new DeviceRegistrationCommandFacade(registerDispatch, unregisterDispatch);
    }
}
