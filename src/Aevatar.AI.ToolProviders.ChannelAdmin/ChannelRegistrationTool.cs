using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.ToolProviders.ChannelAdmin;

/// <summary>
/// Tool for NyxID chat to manage ChannelRuntime registrations.
/// ChannelRuntime is no longer a channel credential authority; the supported
/// production path is Lark registration provisioned through Nyx relay.
/// </summary>
public sealed class ChannelRegistrationTool : IAgentTool
{
    // Refactor (iter27/cluster-003-channel-registration-scope-backfill):
    //   Old pattern: tool exposed repair_lark_mirror and rebuild_projection backfilled write candidates from readmodels.
    //   New principle: tool recovery surface is register_lark_via_nyx plus projection-only rebuild_projection.
    private const string DefaultNyxProviderSlug = "api-lark-bot";
    private readonly IServiceProvider _serviceProvider;

    public ChannelRegistrationTool(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string Name => "channel_registrations";

    public string Description =>
        "Manage Aevatar ChannelRuntime registrations for the supported Nyx-backed Lark relay flow. " +
        "Actions: list, register_lark_via_nyx, rebuild_projection, delete. " +
        "Use register_lark_via_nyx for provisioning and rebuild_projection to re-materialize the local registration read model from authoritative actor state. " +
        "Legacy direct callback registration and update_token flows are retired because ChannelRuntime no longer stores channel credentials. " +
        "Do not ask the user for scope_id; it is resolved from the current NyxID request context and should only be supplied explicitly for diagnostics.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "register_lark_via_nyx", "rebuild_projection", "delete"],
              "description": "Action to perform (default: list)."
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "NyxID bot service slug (optional for register_lark_via_nyx; defaults to api-lark-bot)"
            },
            "scope_id": {
              "type": "string",
              "description": "Scope ID for multi-tenant isolation. Normally supplied from the current NyxID request context; only pass explicitly for diagnostics."
            },
            "webhook_base_url": {
              "type": "string",
              "description": "Base URL for Nyx relay callbacks, e.g. 'https://aevatar-console-backend-api.aevatar.ai' (required for register_lark_via_nyx)"
            },
            "app_id": {
              "type": "string",
              "description": "Lark app ID (required for register_lark_via_nyx)"
            },
            "app_secret": {
              "type": "string",
              "description": "Lark app secret (required for register_lark_via_nyx)"
            },
            "verification_token": {
              "type": "string",
              "description": "Lark verification token (optional for register_lark_via_nyx, but pass it through when the backend requires it)"
            },
            "label": {
              "type": "string",
              "description": "Human-readable label for the Nyx channel bot (optional)"
            },
            "reason": {
              "type": "string",
              "description": "Optional operator reason for rebuild_projection"
            },
            "registration_id": {
              "type": "string",
              "description": "Registration ID for delete"
            },
            "confirm": {
              "type": "boolean",
              "description": "Must be true to execute delete. First call delete without confirm to inspect the registration."
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        var action = GetStr(root, "action") ?? "list";

        return action switch
        {
            "list" => await ExecuteWithQueryAsync(queryPort => ListAsync(queryPort, ct)),
            "register_lark_via_nyx" => await RegisterLarkViaNyxAsync(token, root, ct),
            "rebuild_projection" => await ExecuteWithStoreAsync((queryPort, actorRuntime, dispatchPort) => RebuildProjectionAsync(actorRuntime, dispatchPort, root, ct)),
            "delete" => await ExecuteWithStoreAsync((queryPort, actorRuntime, dispatchPort) => DeleteAsync(queryPort, actorRuntime, dispatchPort, root, ct)),
            "register" => RetiredActionError("Direct callback registration is retired. Use action=register_lark_via_nyx."),
            "update_token" => RetiredActionError("update_token is retired. ChannelRuntime no longer stores or refreshes channel credentials."),
            _ => await ExecuteWithQueryAsync(queryPort => ListAsync(queryPort, ct)),
        };
    }

    private async Task<string> ExecuteWithQueryAsync(Func<IChannelBotRegistrationQueryPort, Task<string>> operation)
    {
        var queryPort = _serviceProvider.GetService<IChannelBotRegistrationQueryPort>();
        if (queryPort is null)
            return """{"error":"Channel runtime not available. IChannelBotRegistrationQueryPort is not registered in DI."}""";

        return await operation(queryPort);
    }

    private async Task<string> ExecuteWithStoreAsync(
        Func<IChannelBotRegistrationQueryPort, IActorRuntime, IActorDispatchPort, Task<string>> operation)
    {
        var queryPort = _serviceProvider.GetService<IChannelBotRegistrationQueryPort>();
        var actorRuntime = _serviceProvider.GetService<IActorRuntime>();
        var dispatchPort = _serviceProvider.GetService<IActorDispatchPort>();
        if (queryPort is null || actorRuntime is null || dispatchPort is null)
        {
            return """{"error":"Channel runtime not available. IChannelBotRegistrationQueryPort, IActorRuntime, or IActorDispatchPort is not registered in DI."}""";
        }

        return await operation(queryPort, actorRuntime, dispatchPort);
    }

    private static string? GetStr(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolveNyxProviderSlug(JsonElement args)
    {
        var slug = GetStr(args, "nyx_provider_slug")?.Trim();
        return string.IsNullOrWhiteSpace(slug) ? DefaultNyxProviderSlug : slug;
    }

    private static ToolScopeResolution ResolveToolScopeId(JsonElement args, bool required)
    {
        var explicitScopeId = NormalizeOptional(GetStr(args, "scope_id"));
        var contextScopeId = NormalizeOptional(AgentToolRequestContext.ScopeId);
        if (explicitScopeId is not null &&
            contextScopeId is not null &&
            !string.Equals(explicitScopeId, contextScopeId, StringComparison.Ordinal))
        {
            return new ToolScopeResolution(null, "scope_id does not match the current NyxID request scope");
        }

        var resolved = explicitScopeId ?? contextScopeId;
        if (required && resolved is null)
            return new ToolScopeResolution(null, "scope_id is required from the current NyxID request context");

        return new ToolScopeResolution(resolved, null);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string SerializeError(string error) =>
        JsonSerializer.Serialize(new { error });

    private sealed record ToolScopeResolution(string? ScopeId, string? Error);

    private static string RetiredActionError(string message) =>
        JsonSerializer.Serialize(new
        {
            error_code = "retired_action",
            error = message,
        });

    private static string SerializeLarkRegistrationPayload(
        string status,
        string registrationId,
        string nyxProviderSlug,
        string nyxChannelBotId,
        string nyxAgentApiKeyId,
        string nyxConversationRouteId,
        string relayCallbackUrl,
        string webhookUrl,
        string error,
        string note) =>
        JsonSerializer.Serialize(new
        {
            status,
            registration_id = registrationId,
            platform = "lark",
            nyx_provider_slug = nyxProviderSlug,
            nyx_channel_bot_id = nyxChannelBotId,
            nyx_agent_api_key_id = nyxAgentApiKeyId,
            nyx_conversation_route_id = nyxConversationRouteId,
            relay_callback_url = relayCallbackUrl,
            webhook_url = webhookUrl,
            error,
            note,
        });

    private async Task<string> ListAsync(IChannelBotRegistrationQueryPort queryPort, CancellationToken ct)
    {
        var registrations = await queryPort.QueryAllAsync(ct);
        var result = registrations.Select(entry => new
        {
            id = entry.Id,
            platform = entry.Platform,
            registration_mode = "nyx_relay_webhook",
            nyx_provider_slug = entry.NyxProviderSlug,
            scope_id = entry.ScopeId,
            webhook_url = entry.WebhookUrl,
            callback_url = string.Empty,
            nyx_channel_bot_id = entry.NyxChannelBotId,
            nyx_agent_api_key_id = entry.NyxAgentApiKeyId,
            nyx_conversation_route_id = entry.NyxConversationRouteId,
        }).ToList();

        return JsonSerializer.Serialize(
            new { registrations = result, total = result.Count },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }

    private async Task<string> RegisterLarkViaNyxAsync(
        string accessToken,
        JsonElement args,
        CancellationToken ct)
    {
        var provisioningService = _serviceProvider.GetService<INyxLarkProvisioningService>();
        if (provisioningService is null)
            return """{"error":"Nyx-backed Lark provisioning service is not registered."}""";

        var scopeResolution = ResolveToolScopeId(args, required: true);
        if (scopeResolution.Error is not null)
            return SerializeError(scopeResolution.Error);

        var result = await provisioningService.ProvisionAsync(
            new NyxLarkProvisioningRequest(
                AccessToken: accessToken,
                AppId: GetStr(args, "app_id")?.Trim() ?? string.Empty,
                AppSecret: GetStr(args, "app_secret")?.Trim() ?? string.Empty,
                VerificationToken: GetStr(args, "verification_token")?.Trim() ?? string.Empty,
                WebhookBaseUrl: GetStr(args, "webhook_base_url")?.Trim() ?? string.Empty,
                ScopeId: scopeResolution.ScopeId!,
                Label: GetStr(args, "label")?.Trim() ?? string.Empty,
                NyxProviderSlug: GetStr(args, "nyx_provider_slug")?.Trim() ?? string.Empty),
            ct);

        return SerializeLarkRegistrationPayload(
            status: result.Status,
            registrationId: result.RegistrationId ?? string.Empty,
            nyxProviderSlug: ResolveNyxProviderSlug(args),
            nyxChannelBotId: result.NyxChannelBotId ?? string.Empty,
            nyxAgentApiKeyId: result.NyxAgentApiKeyId ?? string.Empty,
            nyxConversationRouteId: result.NyxConversationRouteId ?? string.Empty,
            relayCallbackUrl: result.RelayCallbackUrl ?? string.Empty,
            webhookUrl: result.WebhookUrl ?? string.Empty,
            error: result.Error ?? string.Empty,
            note: result.Note ?? string.Empty);
    }

    private async Task<string> RebuildProjectionAsync(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        JsonElement args,
        CancellationToken ct)
    {
        // Refactor (iter27/cluster-003-channel-registration-scope-backfill):
        //   Old pattern: rebuild_projection read query state and dispatched repair writes.
        //   New principle: dispatch only the rebuild command; readmodel visibility is observed later.
        await ChannelBotRegistrationStoreCommands.DispatchRebuildProjectionAsync(
            actorRuntime,
            dispatchPort,
            GetStr(args, "reason")?.Trim() ?? "tool_manual_rebuild",
            ct);

        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            actor_id = ChannelBotRegistrationGAgent.WellKnownId,
            note = "Projection rebuild dispatched from authoritative channel-bot-registration-store state. Query-side registrations may take a moment to refresh.",
        });
    }

    private async Task<string> DeleteAsync(
        IChannelBotRegistrationQueryPort queryPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        JsonElement args,
        CancellationToken ct)
    {
        var registrationId = GetStr(args, "registration_id") ?? GetStr(args, "id");
        if (string.IsNullOrWhiteSpace(registrationId))
            return """{"error":"'registration_id' is required for delete"}""";

        var exists = await queryPort.GetAsync(registrationId, ct);
        if (exists is null)
            return JsonSerializer.Serialize(new { error = $"Registration '{registrationId}' not found" });

        var confirm = args.TryGetProperty("confirm", out var confirmValue) && confirmValue.ValueKind == JsonValueKind.True;
        if (!confirm)
        {
            return JsonSerializer.Serialize(new
            {
                status = "confirm_required",
                registration_id = exists.Id,
                platform = exists.Platform,
                registration_mode = "nyx_relay_webhook",
                nyx_provider_slug = exists.NyxProviderSlug,
                scope_id = exists.ScopeId,
                nyx_channel_bot_id = exists.NyxChannelBotId,
                nyx_agent_api_key_id = exists.NyxAgentApiKeyId,
                nyx_conversation_route_id = exists.NyxConversationRouteId,
                note = "Call again with confirm=true to delete this registration. This action cannot be undone.",
            });
        }

        await ChannelBotRegistrationStoreCommands.DispatchUnregisterAsync(
            actorRuntime,
            dispatchPort,
            registrationId,
            ct);

        // Refactor (iter6/cluster-014):
        //   Old pattern: Delete slept and re-read the projection to upgrade accepted into deleted.
        //   New principle: Unregister ACK is accepted-only; deletion visibility is observed by follow-up query.
        return JsonSerializer.Serialize(new
        {
            status = "accepted",
            registration_id = registrationId,
            note = "Unregister accepted. Projection is propagating; try 'list' in a few seconds to confirm the registration is gone.",
        });
    }
}
