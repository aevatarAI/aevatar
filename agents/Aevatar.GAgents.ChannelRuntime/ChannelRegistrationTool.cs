using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tool for NyxId chat to manage Aevatar channel bot registrations.
/// Allows the agent to register, list, and delete channel bots so users
/// don't need to call the REST API manually.
/// </summary>
public sealed class ChannelRegistrationTool : IAgentTool
{
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly IActorRuntime _actorRuntime;

    public ChannelRegistrationTool(
        IChannelBotRegistrationQueryPort queryPort,
        IActorRuntime actorRuntime)
    {
        _queryPort = queryPort;
        _actorRuntime = actorRuntime;
    }

    public string Name => "channel_registrations";

    public string Description =>
        "Manage Aevatar channel bot registrations (Lark, Telegram, Discord). " +
        "Actions: list, register, delete. " +
        "Use this to set up platform bot callbacks so users can chat with agents via messaging apps.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "register", "delete"],
              "description": "Action to perform (default: list)"
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
              "description": "Registration ID (for delete)"
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

        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = GetStr(root, "action") ?? "list";

        return action switch
        {
            "list" => await ListAsync(ct),
            "register" => await RegisterAsync(token, root, ct),
            "delete" => await DeleteAsync(root, ct),
            _ => await ListAsync(ct),
        };
    }

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<string> ListAsync(CancellationToken ct)
    {
        var registrations = await _queryPort.QueryAllAsync(ct);
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

    private async Task<string> RegisterAsync(string token, JsonElement args, CancellationToken ct)
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

        // Snapshot existing IDs before dispatch so we can identify the NEW entry after.
        var existingIds = (await _queryPort.QueryAllAsync(ct)).Select(e => e.Id).ToHashSet();

        var actor = await _actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await _actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                        ChannelBotRegistrationGAgent.WellKnownId);

        var cmd = new ChannelBotRegisterCommand
        {
            Platform = platform.Trim().ToLowerInvariant(),
            NyxProviderSlug = nyxProviderSlug.Trim(),
            NyxUserToken = token,
            VerificationToken = GetStr(args, "verification_token")?.Trim() ?? string.Empty,
            ScopeId = GetStr(args, "scope_id")?.Trim() ?? string.Empty,
            WebhookUrl = webhookUrl,
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

        // Poll for the NEW registration ID (not in the pre-dispatch snapshot).
        // Retry up to 5 times with 500ms delay to bridge eventual consistency.
        string? registrationId = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(500, ct);
            var all = await _queryPort.QueryAllAsync(ct);
            var newEntry = all.FirstOrDefault(e => !existingIds.Contains(e.Id));
            if (newEntry != null)
            {
                registrationId = newEntry.Id;
                break;
            }
        }

        if (registrationId != null)
        {
            return JsonSerializer.Serialize(new
            {
                status = "registered",
                registration_id = registrationId,
                platform = cmd.Platform,
                nyx_provider_slug = cmd.NyxProviderSlug,
                callback_url = $"{callbackPath}/{registrationId}",
                webhook_url = !string.IsNullOrWhiteSpace(webhookUrl) ? $"{webhookUrl}/{registrationId}" : "",
            });
        }

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            platform = cmd.Platform,
            nyx_provider_slug = cmd.NyxProviderSlug,
            callback_url_pattern = $"{callbackPath}/{{registration_id}}",
            note = "Registration accepted but ID not yet available. Use 'list' action to retrieve it.",
        });
    }

    private async Task<string> DeleteAsync(JsonElement args, CancellationToken ct)
    {
        var registrationId = GetStr(args, "registration_id") ?? GetStr(args, "id");
        if (string.IsNullOrWhiteSpace(registrationId))
            return """{"error":"'registration_id' is required for delete"}""";

        var exists = await _queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return JsonSerializer.Serialize(new { error = $"Registration '{registrationId}' not found" });

        // Require explicit confirm=true. First call without confirm shows details for user review.
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

        var actor = await _actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
                    ?? await _actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
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
