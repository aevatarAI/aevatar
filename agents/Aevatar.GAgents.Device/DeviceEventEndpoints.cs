using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Household;
using Google.Protobuf.WellKnownTypes;
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
    /// 3. Parse CallbackPayload → DeviceInbound.
    /// 4. Synchronous dispatch to HouseholdEntity actor.
    /// 5. Return 202 Accepted (or 502 on dispatch failure — NyxID retries at transport level).
    /// </summary>
    private static async Task<IResult> HandleDeviceCallbackAsync(
        HttpContext http,
        string registrationId,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IDeviceRegistrationQueryPort queryPort,
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

        // HMAC verification
        if (!VerifyHmacSignature(http, bodyBytes, registration, options.Value))
        {
            logger.LogWarning("Device event HMAC verification failed: registration={RegistrationId}", registrationId);
            return Results.Unauthorized();
        }

        // Parse callback payload
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

        // Resolve HouseholdEntity actor
        var householdActorId = $"household-{registration.ScopeId}";

        // Synchronous dispatch — failure returns 502 so NyxID retries at transport level
        try
        {
            await DispatchToHouseholdAsync(inbound, householdActorId, actorRuntime, loggerFactory);
        }
        catch (Exception ex)
        {
            var logger2 = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.DeviceEvent");
            logger2.LogError(ex, "Device event dispatch failed: event_id={EventId}", inbound.EventId);
            return Results.StatusCode(502);
        }

        return Results.Accepted();
    }

    /// <summary>
    /// Gets or creates the well-known DeviceRegistrationGAgent singleton actor.
    /// Lifecycle: created on first request, never destroyed (long-lived fact owner per CLAUDE.md).
    /// Thread safety: Orleans grain runtime guarantees single-activation, so concurrent
    /// CreateAsync calls from multiple requests safely converge to the same grain.
    /// </summary>
    private static async Task<IActor> GetOrCreateRegistrationActorAsync(IActorRuntime actorRuntime)
    {
        return await actorRuntime.GetAsync(DeviceRegistrationGAgent.WellKnownId)
               ?? await actorRuntime.CreateAsync<DeviceRegistrationGAgent>(DeviceRegistrationGAgent.WellKnownId);
    }

    internal static bool VerifyHmacSignature(
        HttpContext http,
        byte[] bodyBytes,
        DeviceRegistrationEntry registration,
        DeviceEventOptions options)
    {
        if (options.SkipHmacVerification)
            return true;

        if (!http.Request.Headers.TryGetValue("X-NyxID-Signature", out var signatureHeader)
            || string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        if (string.IsNullOrEmpty(registration.HmacKey))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(registration.HmacKey);
        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = hmac.ComputeHash(bodyBytes);
        var computedSignature = Convert.ToHexStringLower(computedHash);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedSignature),
            Encoding.UTF8.GetBytes(signatureHeader.ToString()));
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

        // content.text contains the raw device event JSON (same in both old and new format)
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

        // Parse the inner device event JSON from content.text
        using var innerDoc = JsonDocument.Parse(contentText);
        var inner = innerDoc.RootElement;

        var eventId = inner.TryGetProperty("event_id", out var eid) ? eid.GetString() ?? string.Empty : string.Empty;
        var source = inner.TryGetProperty("source", out var src) ? src.GetString() ?? string.Empty : string.Empty;
        var eventType = inner.TryGetProperty("event_type", out var et) ? et.GetString() ?? string.Empty : string.Empty;
        var timestamp = inner.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? string.Empty : string.Empty;

        return new DeviceInbound
        {
            EventId = eventId,
            Source = source,
            EventType = eventType,
            Timestamp = timestamp,
            PayloadJson = contentText,
            DeviceId = senderId,
        };
    }

    /// <summary>
    /// Dispatches a device event to the HouseholdEntity actor (single attempt).
    /// On failure the caller returns 502, allowing NyxID to retry at transport level.
    /// Lifecycle: the household actor is created on first request for a given scope,
    /// never destroyed (long-lived fact owner per CLAUDE.md).
    /// Thread safety: Orleans grain runtime guarantees single-activation, so concurrent
    /// CreateAsync calls from multiple requests safely converge to the same grain.
    /// </summary>
    private static async Task DispatchToHouseholdAsync(
        DeviceInbound inbound,
        string householdActorId,
        IActorRuntime actorRuntime,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.ChannelRuntime.DeviceEvent");

        var actor = await actorRuntime.GetAsync(householdActorId)
                    ?? await actorRuntime.CreateAsync<HouseholdEntity>(householdActorId);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(inbound),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope);

        logger.LogInformation(
            "Device event dispatched: event_id={EventId}, target={HouseholdActorId}",
            inbound.EventId, householdActorId);
    }

    // ─── Registration CRUD ───

    private static async Task<IResult> HandleRegisterDeviceAsync(
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
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

        var actor = await GetOrCreateRegistrationActorAsync(actorRuntime);

        // Dispatch register command to actor
        var cmd = new DeviceRegisterCommand
        {
            ScopeId = request.ScopeId.Trim(),
            HmacKey = request.HmacKey?.Trim() ?? string.Empty,
            NyxConversationId = request.NyxConversationId?.Trim() ?? string.Empty,
            Description = request.Description?.Trim() ?? string.Empty,
        };

        var cmdEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(cmd),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(cmdEnvelope);

        // Command accepted — the projection pipeline will materialize the read model.
        // Return accepted with the command details (eventual consistency).
        return Results.Accepted(value: new
        {
            status = "accepted",
            scope_id = request.ScopeId.Trim(),
            description = request.Description?.Trim() ?? string.Empty,
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
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IDeviceRegistrationQueryPort queryPort,
        CancellationToken ct)
    {
        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return Results.NotFound(new { error = "Registration not found" });

        var actor = await GetOrCreateRegistrationActorAsync(actorRuntime);
        var cmd = new DeviceUnregisterCommand { RegistrationId = registrationId };
        var cmdEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(cmd),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(cmdEnvelope);
        return Results.Ok(new { status = "deleted" });
    }

    private sealed record DeviceRegistrationRequest(
        string? ScopeId,
        string? HmacKey,
        string? NyxConversationId,
        string? Description);
}
