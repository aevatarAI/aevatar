using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tool for NyxId chat to manage Aevatar channel bot registrations.
/// Allows the agent to register, list, and delete channel bots so users
/// don't need to call the REST API manually.
/// </summary>
public sealed class ChannelRegistrationTool : IAgentTool
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Constructor takes IServiceProvider only. Dependencies (IChannelBotRegistrationQueryPort,
    /// IActorRuntime) are resolved lazily in ExecuteAsync — at call time the Orleans silo is
    /// fully started and all services are available. This avoids construction-time DI failures
    /// when the tool is instantiated during grain activation.
    /// </summary>
    public ChannelRegistrationTool(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string Name => "channel_registrations";

    public string Description =>
        "Manage Aevatar channel bot registrations (Lark, Telegram, Discord). " +
        "Actions: list, register, delete, update_token. " +
        "Use this to set up platform bot callbacks so users can chat with agents via messaging apps. " +
        "Use update_token to refresh the NyxID access token on an existing registration when the old token expires.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "register", "delete", "update_token"],
              "description": "Action to perform (default: list). Use update_token to refresh the NyxID token on an existing registration."
            },
            "platform": {
              "type": "string",
              "enum": ["lark", "telegram", "discord"],
              "description": "Platform (for register)"
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "NyxID bot service slug, e.g. 'api-lark-bot' (for register)"
            },
            "verification_token": {
              "type": "string",
              "description": "Platform verification token (for register, optional)"
            },
            "scope_id": {
              "type": "string",
              "description": "Scope ID for multi-tenant isolation (for register, optional)"
            },
            "webhook_base_url": {
              "type": "string",
              "description": "Base URL for webhook callbacks, e.g. 'https://aevatar-console-backend-api.aevatar.ai' (for register)"
            },
            "registration_id": {
              "type": "string",
              "description": "Registration ID (for delete, update_token)"
            },
            "confirm": {
              "type": "boolean",
              "description": "Must be true to execute delete. First call delete without confirm to see details, then call again with confirm=true."
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        // Lazy resolve at call time — silo is fully started, all services available
        var queryPort = _serviceProvider.GetService<IChannelBotRegistrationQueryPort>();
        var actorRuntime = _serviceProvider.GetService<IActorRuntime>();
        if (queryPort is null || actorRuntime is null)
            return """{"error":"Channel runtime not available. IChannelBotRegistrationQueryPort or IActorRuntime not registered in DI."}""";

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = GetStr(root, "action") ?? "list";

        return action switch
        {
            "list" => await ListAsync(queryPort, ct),
            "register" => await RegisterAsync(queryPort, actorRuntime, token, root, ct),
            "delete" => await DeleteAsync(queryPort, actorRuntime, root, ct),
            "update_token" => await UpdateTokenAsync(queryPort, actorRuntime, token, root, ct),
            _ => await ListAsync(queryPort, ct),
        };
    }

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<string> ListAsync(IChannelBotRegistrationQueryPort queryPort, CancellationToken ct)
    {
        var registrations = await queryPort.QueryAllAsync(ct);
        var result = registrations.Select(e => new
        {
            id = e.Id,
            platform = e.Platform,
            nyx_provider_slug = e.NyxProviderSlug,
            scope_id = e.ScopeId,
            webhook_url = e.WebhookUrl,
            callback_url = $"/api/channels/{e.Platform}/callback/{e.Id}",
        }).ToList();

        return JsonSerializer.Serialize(new { registrations = result, total = result.Count },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }

    private async Task<string> RegisterAsync(
        IChannelBotRegistrationQueryPort queryPort, IActorRuntime actorRuntime,
        string token, JsonElement args, CancellationToken ct)
    {
        var platform = GetStr(args, "platform");
        if (string.IsNullOrWhiteSpace(platform))
            return """{"error":"'platform' is required for register"}""";

        var nyxProviderSlug = GetStr(args, "nyx_provider_slug");
        if (string.IsNullOrWhiteSpace(nyxProviderSlug))
            return """{"error":"'nyx_provider_slug' is required for register (e.g. 'api-lark-bot')"}""";

        var webhookBaseUrl = GetStr(args, "webhook_base_url") ?? "";
        var callbackPath = $"/api/channels/{platform.Trim().ToLowerInvariant()}/callback";
        var webhookUrl = !string.IsNullOrWhiteSpace(webhookBaseUrl)
            ? webhookBaseUrl.TrimEnd('/') + callbackPath
            : string.Empty;

        // Ensure projection scope is activated before dispatch
        var projectionPort = _serviceProvider.GetService<ChannelBotRegistrationProjectionPort>();
        if (projectionPort != null)
            await projectionPort.EnsureProjectionForActorAsync(ChannelBotRegistrationGAgent.WellKnownId, ct);

        var registrationId = Guid.NewGuid().ToString("N");

        var actor = await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId);

        var cmd = new ChannelBotRegisterCommand
        {
            Platform = platform.Trim().ToLowerInvariant(),
            NyxProviderSlug = nyxProviderSlug.Trim(),
            NyxUserToken = token,
            VerificationToken = GetStr(args, "verification_token")?.Trim() ?? string.Empty,
            ScopeId = GetStr(args, "scope_id")?.Trim() ?? string.Empty,
            WebhookUrl = webhookUrl,
            RequestedId = registrationId,
        };

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(cmd),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope);

        // Projection scope is now activated. Poll for the document to appear.
        // The projector runs async via the materialization scope agent.
        string? confirmedId = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500, ct);
            var entry = await queryPort.GetAsync(registrationId, ct);
            if (entry != null)
            {
                confirmedId = entry.Id;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = confirmedId != null ? "registered" : "accepted",
            registration_id = registrationId,
            platform = cmd.Platform,
            nyx_provider_slug = cmd.NyxProviderSlug,
            callback_url = $"{callbackPath}/{registrationId}",
            webhook_url = !string.IsNullOrWhiteSpace(webhookUrl) ? $"{webhookUrl}/{registrationId}" : "",
            note = confirmedId == null ? "Registration submitted but projection not yet confirmed. Try 'list' after a few seconds." : "",
        });
    }

    private async Task<string> UpdateTokenAsync(
        IChannelBotRegistrationQueryPort queryPort, IActorRuntime actorRuntime,
        string token, JsonElement args, CancellationToken ct)
    {
        var registrationId = GetStr(args, "registration_id") ?? GetStr(args, "id");
        if (string.IsNullOrWhiteSpace(registrationId))
            return """{"error":"'registration_id' is required for update_token"}""";

        var before = await queryPort.GetAsync(registrationId, ct);
        if (before is null)
            return JsonSerializer.Serialize(new { error = $"Registration '{registrationId}' not found" });

        var oldToken = before.NyxUserToken;

        var actor = await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId);

        var cmd = new ChannelBotUpdateTokenCommand
        {
            RegistrationId = registrationId,
            NyxUserToken = token,
        };

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(cmd),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope);

        // Poll projection to confirm the token was actually committed.
        // The actor silently drops the command if the registration is not in its
        // state, so we cannot trust HandleEventAsync returning without error.
        var confirmed = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500, ct);
            var after = await queryPort.GetAsync(registrationId, ct);
            if (after is not null && after.NyxUserToken != oldToken)
            {
                confirmed = true;
                break;
            }
        }

        if (!confirmed)
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                registration_id = registrationId,
                error = "Token update was dispatched but not confirmed — the projection did not reflect the change. " +
                        "The registration may not exist in the actor's state. Try delete + re-register.",
            });
        }

        return JsonSerializer.Serialize(new
        {
            status = "token_updated",
            registration_id = registrationId,
            platform = before.Platform,
            note = "NyxID access token has been refreshed. Bot replies should work again.",
        });
    }

    private async Task<string> DeleteAsync(
        IChannelBotRegistrationQueryPort queryPort, IActorRuntime actorRuntime,
        JsonElement args, CancellationToken ct)
    {
        var registrationId = GetStr(args, "registration_id") ?? GetStr(args, "id");
        if (string.IsNullOrWhiteSpace(registrationId))
            return """{"error":"'registration_id' is required for delete"}""";

        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return JsonSerializer.Serialize(new { error = $"Registration '{registrationId}' not found" });

        var confirm = args.TryGetProperty("confirm", out var cv) && cv.ValueKind == JsonValueKind.True;
        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                status = "confirm_required",
                registration_id = exists.Id,
                platform = exists.Platform,
                nyx_provider_slug = exists.NyxProviderSlug,
                scope_id = exists.ScopeId,
                note = "Call again with confirm=true to delete this registration. This action cannot be undone.",
            });
        }

        var actor = await actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId);

        var cmd = new ChannelBotUnregisterCommand { RegistrationId = registrationId };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(cmd),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };

        await actor.HandleEventAsync(envelope);
        return JsonSerializer.Serialize(new { status = "deleted", registration_id = registrationId });
    }
}
