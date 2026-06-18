using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Household;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Device;

public sealed class DeviceEventOptions
{
    public bool SkipHmacVerification { get; set; }

    public TimeSpan CallbackFreshnessWindow { get; set; } = TimeSpan.FromSeconds(10);
}

public static class DeviceEventEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/device-events").WithTags("DeviceEvents");

        // Device callback from NyxID relay. Authentication is HMAC-based (X-NyxID-Signature),
        // not JWT — the endpoint must stay anonymous so the fallback policy does not reject
        // legitimate device events before we can verify them.
        group.MapPost("/{registrationId}", HandleDeviceCallbackAsync).AllowAnonymous();
        group.MapPost("/registrations", HandleRegisterDeviceAsync).RequireAuthorization();
        group.MapGet("/registrations", HandleListDeviceRegistrationsAsync).RequireAuthorization();
        group.MapDelete("/registrations/{registrationId}", HandleDeleteDeviceRegistrationAsync).RequireAuthorization();

        return app;
    }

    private static readonly JsonSerializerOptions RegistrationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Receives a device event callback from NyxID relay.
    /// 1. Lookup registration from projection read model.
    /// 2. HMAC verification (configurable).
    /// 3. Parse CallbackPayload → DeviceInbound known typed payload.
    /// 4. Reject unknown event_type values before command facade dispatch.
    /// 5. Return 202 Accepted (or 502 on dispatch failure — NyxID retries at transport level).
    /// </summary>
    // Refactor (iter47/issue-873-device-endpoint-direct-runtime-dispatch):
    //   Old pattern: Device HTTP endpoint resolves/creates actors, builds EventEnvelope directly, and dispatches through runtime/dispatch ports.
    //   New principle: Endpoint delegates to typed application command facade; target resolution, envelope construction, dispatch receipt, and resource ownership live behind command skeleton contracts. No callback-time auto-create.
    private static async Task<IResult> HandleDeviceCallbackAsync(
        HttpContext http,
        string registrationId,
        [FromServices] IDeviceRegistrationQueryPort queryPort,
        [FromServices] IDeviceCallbackCommandService callbackCommandService,
        [FromServices] IOptions<DeviceEventOptions> options,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.DeviceEvent");

        // Lookup registration from projection read model
        var registration = await queryPort.GetAsync(registrationId, ct);
        if (registration is null)
            return Results.NotFound(new { error = "Registration not found" });

        // Read request body
        http.Request.EnableBuffering();
        byte[] bodyBytes;
        using (var ms = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(ms, ct);
            bodyBytes = ms.ToArray();
        }

        var now = DateTimeOffset.UtcNow;
        var admissionResult = AdmitCallback(http, bodyBytes, registration, options.Value, now);
        if (!admissionResult.Succeeded)
        {
            logger.LogWarning(
                "Device event callback admission failed: registration={RegistrationId}, reason={Reason}",
                registrationId,
                admissionResult.Error);
            return admissionResult.Error == DeviceCallbackAdmissionError.SignatureRejected
                ? Results.Unauthorized()
                : Results.BadRequest(new { error = admissionResult.Error });
        }

        var admission = admissionResult.Admission!;
        LogHeaderTimestampDiagnostic(logger, http, admission);

        // Parse callback payload into the v1 typed DeviceInbound allowlist.
        // Unknown event_type values are rejected here, before actor/read-model/projection dispatch.
        DeviceInbound inbound;
        try
        {
            inbound = ParseCallbackPayload(bodyBytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse device event callback payload");
            return Results.BadRequest(new { error = "Invalid callback payload" });
        }

        logger.LogInformation(
            "Device event received: event_id={EventId}, source={Source}, type={EventType}",
            inbound.EventId, inbound.Source, inbound.EventType);

        // Dispatch admission + target resolution live behind the typed command facade.
        try
        {
            var dispatch = await callbackCommandService.DispatchCallbackAsync(
                new DeviceCallbackDispatchCommand(registrationId, inbound, admission),
                ct);
            if (!dispatch.Succeeded)
            {
                return dispatch.Error switch
                {
                    DeviceCallbackCommandStartError.RegistrationNotFound =>
                        Results.NotFound(new { error = "Registration not found" }),
                    DeviceCallbackCommandStartError.RegistrationNotAdmitted =>
                        Results.Conflict(new { error = "Registration is not admitted for device callbacks" }),
                    DeviceCallbackCommandStartError.TargetActorUnavailable =>
                        Results.Conflict(new { error = "Device callback target is unavailable" }),
                    _ => Results.StatusCode(502),
                };
            }

            return Results.Accepted(value: new
            {
                status = "accepted",
                actor_id = dispatch.Receipt!.ActorId,
                command_id = dispatch.Receipt.CommandId,
                correlation_id = dispatch.Receipt.CorrelationId,
                registration_id = dispatch.Receipt.RegistrationId,
            });
        }
        catch (Exception ex)
        {
            var logger2 = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.DeviceEvent");
            logger2.LogError(ex, "Device event dispatch failed: event_id={EventId}", inbound.EventId);
            return Results.StatusCode(502);
        }
    }

    internal static DeviceCallbackAdmissionResult AdmitCallback(
        HttpContext http,
        byte[] bodyBytes,
        DeviceRegistrationEntry registration,
        DeviceEventOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bodyBytes);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            if (!TryReadCallbackAdmissionFacts(
                    bodyBytes,
                    registration.Id ?? string.Empty,
                    out var admission,
                    out var parseError))
            {
                return DeviceCallbackAdmissionResult.Failure(parseError);
            }

            var admitted = admission!;
            if (!VerifyHmacSignature(http, bodyBytes, registration, options, admitted))
                return DeviceCallbackAdmissionResult.Failure(DeviceCallbackAdmissionError.SignatureRejected);

            if (!admitted.IsFreshAt(now, options.CallbackFreshnessWindow))
                return DeviceCallbackAdmissionResult.Failure(DeviceCallbackAdmissionError.StaleSignedBodyTimestamp);

            return DeviceCallbackAdmissionResult.Success(admitted);
        }
        catch (JsonException)
        {
            return DeviceCallbackAdmissionResult.Failure(DeviceCallbackAdmissionError.InvalidDeliveryId);
        }
    }

    internal static bool VerifyHmacSignature(
        HttpContext http,
        byte[] bodyBytes,
        DeviceRegistrationEntry registration,
        DeviceEventOptions options,
        DeviceCallbackAdmission? admission = null)
    {
        if (options.SkipHmacVerification)
            return true;

        if (!http.Request.Headers.TryGetValue("X-NyxID-Signature", out var signatureHeader)
            || string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        if (string.IsNullOrEmpty(registration.HmacKey))
            return false;

        if (admission is null)
        {
            try
            {
                if (!TryReadCallbackAdmissionFacts(
                        bodyBytes,
                        registration.Id ?? string.Empty,
                        out admission,
                        out _))
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        var keyBytes = Encoding.UTF8.GetBytes(registration.HmacKey);
        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = hmac.ComputeHash(BuildSignaturePayload(bodyBytes));
        var computedSignature = Convert.ToHexStringLower(computedHash);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedSignature),
            Encoding.UTF8.GetBytes(signatureHeader.ToString().Trim()));
    }

    internal static byte[] BuildSignaturePayload(byte[] bodyBytes) => bodyBytes;

    private static void LogHeaderTimestampDiagnostic(
        ILogger logger,
        HttpContext http,
        DeviceCallbackAdmission admission)
    {
        if (!http.Request.Headers.TryGetValue("X-NyxID-Timestamp", out var headerValue) ||
            string.IsNullOrWhiteSpace(headerValue))
        {
            return;
        }

        var headerTimestampValue = headerValue.ToString();
        if (!DeviceCallbackAdmission.TryParseSignedBodyTimestamp(headerTimestampValue, out var headerTimestamp))
        {
            logger.LogWarning("Device callback X-NyxID-Timestamp header is not parseable.");
            return;
        }

        if (headerTimestamp != admission.SignedBodyTimestamp)
        {
            logger.LogWarning(
                "Device callback X-NyxID-Timestamp differs from signed body timestamp: header={HeaderTimestamp}, body={BodyTimestamp}",
                headerTimestamp,
                admission.SignedBodyTimestamp);
        }
    }

    private static bool TryReadCallbackAdmissionFacts(
        byte[] bodyBytes,
        string registrationId,
        out DeviceCallbackAdmission? admission,
        out DeviceCallbackAdmissionError error)
    {
        using var doc = JsonDocument.Parse(bodyBytes);
        var root = doc.RootElement;

        var messageId = root.TryGetProperty("message_id", out var messageIdElement)
            ? messageIdElement.GetString() ?? string.Empty
            : string.Empty;

        var contentText = root.TryGetProperty("content", out var content) &&
                          content.TryGetProperty("text", out var contentTextElement)
            ? contentTextElement.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(contentText))
        {
            admission = null;
            error = DeviceCallbackAdmissionError.InvalidDeliveryId;
            return false;
        }

        using var innerDoc = JsonDocument.Parse(contentText);
        var inner = innerDoc.RootElement;
        if (inner.ValueKind != JsonValueKind.Object)
        {
            admission = null;
            error = DeviceCallbackAdmissionError.InvalidDeliveryId;
            return false;
        }

        var timestampValue = root.TryGetProperty("timestamp", out var timestampElement)
            ? timestampElement.GetString() ?? string.Empty
            : string.Empty;
        timestampValue = FirstNonEmpty(timestampValue, GetOptionalString(inner, "timestamp"));
        if (string.IsNullOrWhiteSpace(timestampValue) ||
            !DeviceCallbackAdmission.TryParseSignedBodyTimestamp(timestampValue, out var signedBodyTimestamp))
        {
            admission = null;
            error = DeviceCallbackAdmissionError.InvalidSignedBodyTimestamp;
            return false;
        }

        var eventId = GetOptionalString(inner, "event_id");
        var correlationKey = inner.TryGetProperty("correlation_key", out var correlationKeyElement)
            ? correlationKeyElement.GetString() ?? string.Empty
            : string.Empty;
        var deliveryId = FirstNonEmpty(eventId, correlationKey);
        if (string.IsNullOrWhiteSpace(deliveryId))
        {
            admission = null;
            error = DeviceCallbackAdmissionError.InvalidDeliveryId;
            return false;
        }

        var commandId = FirstNonEmpty(messageId, deliveryId);
        admission = new DeviceCallbackAdmission(
            registrationId,
            commandId,
            deliveryId,
            timestampValue,
            signedBodyTimestamp,
            signedBodyTimestamp);
        error = DeviceCallbackAdmissionError.None;
        return true;
    }

    /// <summary>
    /// Parse NyxID CallbackPayload JSON into DeviceInbound.
    /// NyxID's actual CallbackPayload structure (from channel_relay_service.rs):
    /// {
    ///   "message_id": "nxmsg-uuid",
    ///   "platform": "device",
    ///   "agent": { "api_key_id": "...", "name": "..." },
    ///   "conversation": { "id": "conv-uuid", "platform_id": "...", "conversation_type": "..." },
    ///   "sender": { "platform_id": "device-id", "display_name": "sensor-name" },
    ///   "content": { "content_type": "text", "text": "&lt;raw device event JSON&gt;", "attachments": [] },
    ///   "timestamp": "2026-04-10T12:00:00Z"
    /// }
    /// </summary>
    internal static DeviceInbound ParseCallbackPayload(byte[] bodyBytes)
    {
        using var doc = JsonDocument.Parse(bodyBytes);
        var root = doc.RootElement;

        // content.text contains the raw device event JSON at the adapter boundary.
        var contentText = root.GetProperty("content").GetProperty("text").GetString()
                          ?? throw new JsonException("content.text is required");

        // NyxID sends sender.platform_id (not sender.id)
        var senderId = string.Empty;
        if (root.TryGetProperty("sender", out var sender))
        {
            senderId = sender.TryGetProperty("platform_id", out var pid) ? pid.GetString() ?? string.Empty
                     : sender.TryGetProperty("id", out var sid) ? sid.GetString() ?? string.Empty
                     : string.Empty;
        }

        // Refactor (issue1485/first-slice): Old pattern: pass content.text through as DeviceInbound.PayloadJson.
        // New principle: terminate NyxID callback JSON at the Host/Adapter boundary.
        // Known device events are allowlisted and mapped to typed Protobuf payload cases.
        // Unknown or malformed content is rejected before any EventEnvelope dispatch can happen.
        // This endpoint does not accept a raw payload bag for later actor-side interpretation.
        using var innerDoc = JsonDocument.Parse(contentText);
        var inner = innerDoc.RootElement;
        if (inner.ValueKind != JsonValueKind.Object)
            throw new JsonException("content.text must contain a device event object");

        var eventId = inner.TryGetProperty("event_id", out var eid) ? eid.GetString() ?? string.Empty : string.Empty;
        var source = inner.TryGetProperty("source", out var src) ? src.GetString() ?? string.Empty : string.Empty;
        var eventType = inner.TryGetProperty("event_type", out var et) ? et.GetString() ?? string.Empty : string.Empty;
        var timestamp = inner.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? string.Empty : string.Empty;

        var inbound = new DeviceInbound
        {
            EventId = eventId,
            Source = source,
            EventType = eventType,
            Timestamp = timestamp,
            DeviceId = senderId,
        };

        ApplyKnownPayload(inbound, inner);
        return inbound;
    }

    private static void ApplyKnownPayload(DeviceInbound inbound, JsonElement inner)
    {
        switch (inbound.EventType)
        {
            case "temperature_change":
                inbound.Sensor = new SensorDeviceInboundPayload
                {
                    Temperature = GetOptionalDouble(inner, "temperature"),
                    Humidity = GetOptionalDouble(inner, "humidity"),
                    LightLevel = GetOptionalDouble(inner, "light_level"),
                    MotionDetected = GetOptionalBoolean(inner, "motion_detected",
                        GetOptionalBoolean(inner, "motion")),
                };
                break;
            case "person_detected":
            case "scene_summary":
            case "camera_scene":
                inbound.Camera = new CameraDeviceInboundPayload
                {
                    SceneDescription = GetOptionalString(inner, "description"),
                };
                break;
            case "motion_detected":
                inbound.Motion = new MotionDeviceInboundPayload
                {
                    Detected = GetOptionalBoolean(inner, "detected",
                        GetOptionalBoolean(inner, "motion", defaultValue: true)),
                };
                break;
            case "speech_detected":
                inbound.Speech = new SpeechDeviceInboundPayload
                {
                    Text = GetOptionalString(inner, "text"),
                };
                break;
            case "doorbell_pressed":
            case "smoke_detected":
            case "water_leak_detected":
            case "carbon_monoxide_detected":
            case "glass_break_detected":
            case "lock_tampered":
            case "alarm_triggered":
                inbound.HomeAlert = new HomeAlertDeviceInboundPayload
                {
                    Severity = ResolveHomeAlertSeverity(inbound.EventType, inner),
                    Area = GetOptionalString(inner, "area"),
                    EntityId = GetOptionalString(inner, "entity_id"),
                    CorrelationKey = FirstNonEmpty(
                        GetOptionalString(inner, "correlation_key"),
                        GetOptionalString(inner, "event_id"),
                        inbound.EventId),
                    Summary = FirstNonEmpty(
                        GetOptionalString(inner, "summary"),
                        GetOptionalString(inner, "description"),
                        inbound.EventType),
                };
                break;
            default:
                throw new JsonException($"Unsupported device event_type '{inbound.EventType}'");
        }
    }

    private static DeviceEventSeverity ResolveHomeAlertSeverity(string eventType, JsonElement inner)
    {
        var configuredSeverity = GetOptionalString(inner, "severity");
        if (!string.IsNullOrWhiteSpace(configuredSeverity))
        {
            return configuredSeverity.Trim().ToLowerInvariant() switch
            {
                "critical" or "emergency" or "alarm" => DeviceEventSeverity.Critical,
                "warning" or "warn" => DeviceEventSeverity.Warning,
                "info" or "information" => DeviceEventSeverity.Info,
                _ => DeviceEventSeverity.Unspecified,
            };
        }

        return eventType switch
        {
            "smoke_detected" or
            "water_leak_detected" or
            "carbon_monoxide_detected" or
            "glass_break_detected" or
            "lock_tampered" or
            "alarm_triggered" => DeviceEventSeverity.Critical,
            "doorbell_pressed" => DeviceEventSeverity.Info,
            _ => DeviceEventSeverity.Unspecified,
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static string GetOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static double GetOptionalDouble(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value)
            ? value.GetDouble()
            : 0;
    }

    private static bool GetOptionalBoolean(JsonElement root, string propertyName, bool defaultValue = false)
    {
        return root.TryGetProperty(propertyName, out var value)
            ? value.GetBoolean()
            : defaultValue;
    }

    // ─── Registration CRUD ───

    // Refactor (iter47/issue-873-device-endpoint-direct-runtime-dispatch):
    //   Old pattern: Device HTTP endpoint resolves/creates actors, builds EventEnvelope directly, and dispatches through runtime/dispatch ports.
    //   New principle: Endpoint delegates to typed application command facade; target resolution, envelope construction, dispatch receipt, and resource ownership live behind command skeleton contracts. No callback-time auto-create.
    private static async Task<IResult> HandleRegisterDeviceAsync(
        HttpContext http,
        [FromServices] DeviceRegistrationCommandFacade registrationCommandFacade,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.DeviceRegistration");

        DeviceRegistrationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<DeviceRegistrationRequest>(RegistrationJsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid device registration request payload");
            return Results.BadRequest(new { error = "Invalid JSON" });
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ScopeId))
        {
            return Results.BadRequest(new { error = "scope_id is required" });
        }

        if (string.IsNullOrWhiteSpace(request.DeviceEventTargetActorId))
        {
            return Results.BadRequest(new { error = "device_event_target_actor_id is required" });
        }

        var scopeId = request.ScopeId.Trim();
        var targetActorId = request.DeviceEventTargetActorId.Trim();

        var cmd = new DeviceRegisterCommand
        {
            ScopeId = scopeId,
            HmacKey = request.HmacKey?.Trim() ?? string.Empty,
            NyxConversationId = request.NyxConversationId?.Trim() ?? string.Empty,
            Description = request.Description?.Trim() ?? string.Empty,
            DeviceEventTargetActorId = targetActorId,
        };

        var receipt = await registrationCommandFacade.RegisterAsync(cmd, ct);

        // Command accepted — the projection pipeline will materialize the read model.
        // Return accepted with the command details (eventual consistency).
        return Results.Accepted(value: new
        {
            status = "accepted",
            scope_id = scopeId,
            description = request.Description?.Trim() ?? string.Empty,
            device_event_target_actor_id = targetActorId,
            command_id = receipt.CommandId,
            correlation_id = receipt.CorrelationId,
        });
    }

    private static async Task<IResult> HandleListDeviceRegistrationsAsync(
        [FromServices] IDeviceRegistrationQueryPort queryPort,
        CancellationToken ct)
    {
        var registrations = await queryPort.QueryAllAsync(ct);
        var result = registrations.Select(e => new
        {
            id = e.Id,
            scope_id = e.ScopeId,
            description = e.Description,
            callback_url = $"/api/device-events/{e.Id}",
        });

        return Results.Ok(result);
    }

    private static async Task<IResult> HandleDeleteDeviceRegistrationAsync(
        string registrationId,
        [FromServices] IDeviceRegistrationQueryPort queryPort,
        [FromServices] DeviceRegistrationCommandFacade registrationCommandFacade,
        CancellationToken ct)
    {
        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return Results.NotFound(new { error = "Registration not found" });

        var receipt = await registrationCommandFacade.UnregisterAsync(registrationId, ct);
        return Results.Ok(new
        {
            status = "deleted",
            command_id = receipt.CommandId,
            correlation_id = receipt.CorrelationId,
        });
    }

    private sealed record DeviceRegistrationRequest(
        string? ScopeId,
        string? HmacKey,
        string? NyxConversationId,
        string? Description,
        string? DeviceEventTargetActorId);
}

internal sealed record DeviceCallbackAdmissionResult(
    bool Succeeded,
    DeviceCallbackAdmission? Admission,
    DeviceCallbackAdmissionError Error)
{
    public static DeviceCallbackAdmissionResult Success(DeviceCallbackAdmission admission) =>
        new(true, admission, DeviceCallbackAdmissionError.None);

    public static DeviceCallbackAdmissionResult Failure(DeviceCallbackAdmissionError error) =>
        new(false, null, error);
}

internal enum DeviceCallbackAdmissionError
{
    None = 0,
    SignatureRejected = 1,
    InvalidSignedBodyTimestamp = 2,
    StaleSignedBodyTimestamp = 3,
    InvalidDeliveryId = 4,
}
