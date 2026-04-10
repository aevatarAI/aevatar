using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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

        // Generate registration ID here — actor uses it directly.
        // This avoids polling the projection pipeline (which requires scope agent
        // activation that isn't bootstrapped yet).
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

        // Write registration document directly to InMemory store so the callback
        // endpoint can find it via QueryPort. The projection pipeline's scope agent
        // is not bootstrapped, so we write-through here as a workaround.
        await WriteRegistrationDocumentAsync(cmd, registrationId, ct);

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

    /// <summary>
    /// Write registration document directly to the InMemory store so
    /// HandleCallbackAsync can find it via QueryPort.GetAsync().
    /// This is a workaround for the projection scope agent not being activated.
    /// </summary>
    private async Task WriteRegistrationDocumentAsync(
        ChannelBotRegisterCommand cmd, string registrationId, CancellationToken ct)
    {
        var writer = _serviceProvider.GetService<IProjectionDocumentWriter<ChannelBotRegistrationDocument>>();
        if (writer is null) return;

        var doc = new ChannelBotRegistrationDocument
        {
            Id = registrationId,
            Platform = cmd.Platform,
            NyxProviderSlug = cmd.NyxProviderSlug,
            ScopeId = cmd.ScopeId,
            VerificationToken = cmd.VerificationToken,
            WebhookUrl = cmd.WebhookUrl,
            NyxUserToken = cmd.NyxUserToken,
            StateVersion = 1,
            LastEventId = string.Empty,
            ActorId = ChannelBotRegistrationGAgent.WellKnownId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await writer.UpsertAsync(doc, ct);
    }
}
