using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.AI.ToolProviders.ChannelAdmin;

/// <summary>
/// Tool for NyxID chat to manage ChannelRuntime registrations.
/// ChannelRuntime is no longer a channel credential authority; the supported
/// production path is Lark registration provisioned through Nyx relay.
/// </summary>
public sealed class ChannelRegistrationTool : IAgentTool
{
    // Refactor (iter83/cluster-083-agent-tool-source-root-provider-locator):
    //   Old pattern: tool source captures root IServiceProvider; tools resolve business ports via service locator in ExecuteAsync
    //   New principle: tool source + tools constructor-inject typed contracts; no root provider lookup
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public rebuild surfaces, new=internal Runtime startup helper only
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=manual readmodel rematerialization path, new=startup-owned projection refresh
    // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
    //   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
    //   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
    private const string DefaultNyxProviderSlug = "api-lark-bot";
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly ChannelRegistrationCommandFacade _commandFacade;
    private readonly INyxLarkProvisioningService _provisioningService;

    public ChannelRegistrationTool(
        IChannelBotRegistrationQueryPort queryPort,
        ChannelRegistrationCommandFacade commandFacade,
        INyxLarkProvisioningService provisioningService)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandFacade = commandFacade ?? throw new ArgumentNullException(nameof(commandFacade));
        _provisioningService = provisioningService ?? throw new ArgumentNullException(nameof(provisioningService));
    }

    public string Name => "channel_registrations";

    public string Description =>
        "Manage Aevatar ChannelRuntime registrations for the supported Nyx-backed Lark relay flow. " +
        "Actions: list, register_lark_via_nyx, delete. " +
        "Use register_lark_via_nyx for provisioning. " +
        "Legacy direct callback registration and update_token flows are retired because ChannelRuntime no longer stores channel credentials. " +
        "Do not ask the user for scope_id; it is resolved from the current NyxID request context and should only be supplied explicitly for diagnostics.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "register_lark_via_nyx", "delete"],
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
        var action = NormalizeOptional(GetStr(root, "action")) ?? "list";

        return action switch
        {
            "list" => await ListAsync(_queryPort, ct),
            "register_lark_via_nyx" => await RegisterLarkViaNyxAsync(token, root, ct),
            "delete" => await DeleteAsync(_queryPort, _commandFacade, root, ct),
            "register" => RetiredActionError("Direct callback registration is retired. Use action=register_lark_via_nyx."),
            "update_token" => RetiredActionError("update_token is retired. ChannelRuntime no longer stores or refreshes channel credentials."),
            _ => SerializeError($"Unsupported channel registration action '{action}'."),
        };
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
        var scopeResolution = ResolveToolScopeId(args, required: true);
        if (scopeResolution.Error is not null)
            return SerializeError(scopeResolution.Error);

        var result = await _provisioningService.ProvisionAsync(
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

    private async Task<string> DeleteAsync(
        IChannelBotRegistrationQueryPort queryPort,
        ChannelRegistrationCommandFacade commandFacade,
        JsonElement args,
        CancellationToken ct)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: tool queried then dispatched unregister through raw runtime helper.
        //   New principle: query is only existence/confirmation; write enters command facade.
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

        await commandFacade.UnregisterAsync(registrationId, ct);

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
