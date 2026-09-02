using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.AI.ToolProviders.ChannelAdmin;

/// <summary>
/// Tool for NyxID chat to manage ChannelRuntime registrations.
/// ChannelRuntime is no longer a channel credential authority; the supported
/// production path is channel bot registration provisioned through Nyx relay.
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
    private readonly IChannelBotRegistrationQueryPort _queryPort;
    private readonly ChannelRegistrationCommandFacade _commandFacade;
    private readonly ChannelRelayRegistrationFacade _registrationFacade;

    public ChannelRegistrationTool(
        IChannelBotRegistrationQueryPort queryPort,
        ChannelRegistrationCommandFacade commandFacade,
        ChannelRelayRegistrationFacade registrationFacade)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandFacade = commandFacade ?? throw new ArgumentNullException(nameof(commandFacade));
        _registrationFacade = registrationFacade ?? throw new ArgumentNullException(nameof(registrationFacade));
    }

    public string Name => "channel_registrations";

    public string Description =>
        "Manage Aevatar ChannelRuntime registrations for supported Nyx-backed channel relay flows. " +
        "Actions: list, register_channel_via_nyx, delete. " +
        "Use register_channel_via_nyx with platform=lark or platform=telegram for provisioning. " +
        "Legacy direct callback registration and update_token flows are retired because ChannelRuntime no longer stores channel credentials. " +
        "Do not ask the user for scope_id; it is resolved from the current NyxID request context and should only be supplied explicitly for diagnostics.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "register_channel_via_nyx", "delete"],
              "description": "Action to perform (default: list)."
            },
            "platform": {
              "type": "string",
              "description": "Channel platform to provision for register_channel_via_nyx, for example lark or telegram."
            },
            "nyx_provider_slug": {
              "type": "string",
              "description": "NyxID bot service slug (optional for register_channel_via_nyx; defaults to api-{platform}-bot)"
            },
            "scope_id": {
              "type": "string",
              "description": "Scope ID for multi-tenant isolation. Normally supplied from the current NyxID request context; only pass explicitly for diagnostics."
            },
            "webhook_base_url": {
              "type": "string",
              "description": "Base URL for Nyx relay callbacks, e.g. 'https://aevatar-console-backend-api.aevatar.ai' (required for register_channel_via_nyx)"
            },
            "credentials": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "Platform credential map for register_channel_via_nyx. Lark requires app_id, app_secret, and verification_token, and accepts optional encrypt_key. Telegram accepts bot_token."
            },
            "lark": {
              "type": "object",
              "description": "Lark-scoped credentials for register_channel_via_nyx when platform is lark.",
              "properties": {
                "app_id": { "type": "string" },
                "app_secret": { "type": "string" },
                "verification_token": { "type": "string" },
                "encrypt_key": { "type": "string" }
              }
            },
            "telegram": {
              "type": "object",
              "description": "Telegram-scoped credentials for register_channel_via_nyx when platform is telegram.",
              "properties": {
                "bot_token": { "type": "string" }
              }
            },
            "label": {
              "type": "string",
              "description": "Human-readable label for the Nyx channel bot (optional)"
            },
            "default_skill_name": {
              "type": "string",
              "description": "Optional Ornn skill to bind this bot's plain inbound messages to. When set, every non-command message deterministically runs this skill with the message text as its arguments. Explicit /<skill> triggers and local slash commands still take priority."
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
            "register_channel_via_nyx" => await RegisterChannelViaNyxAsync(token, root, ct),
            "delete" => await DeleteAsync(_queryPort, _commandFacade, root, ct),
            "register" => RetiredActionError("Direct callback registration is retired. Use action=register_channel_via_nyx."),
            "register_lark_via_nyx" => RetiredActionError("register_lark_via_nyx is retired. Use action=register_channel_via_nyx with platform=lark."),
            "update_token" => RetiredActionError("update_token is retired. ChannelRuntime no longer stores or refreshes channel credentials."),
            _ => SerializeError($"Unsupported channel registration action '{action}'."),
        };
    }

    private static string? GetStr(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetNestedStr(JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var nested) || nested.ValueKind != JsonValueKind.Object)
            return null;

        return GetStr(nested, propertyName);
    }

    private static string ResolveNyxProviderSlug(JsonElement args, string platform)
    {
        var slug = NormalizeOptional(GetStr(args, "nyx_provider_slug"));
        return slug ?? $"api-{platform}-bot";
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

    private static string SerializeRegistrationPayload(
        string status,
        string platform,
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
            platform,
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
            default_skill_name = entry.DefaultSkillName,
        }).ToList();

        return JsonSerializer.Serialize(
            new { registrations = result, total = result.Count },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }

    private async Task<string> RegisterChannelViaNyxAsync(
        string accessToken,
        JsonElement args,
        CancellationToken ct)
    {
        var scopeResolution = ResolveToolScopeId(args, required: true);
        if (scopeResolution.Error is not null)
            return SerializeError(scopeResolution.Error);

        var platform = NormalizeOptional(GetStr(args, "platform"));
        if (platform is null)
            return SerializeError("platform is required for register_channel_via_nyx");
        platform = platform.ToLowerInvariant();

        var credentials = BuildCredentialsMap(args, platform);
        var result = await _registrationFacade.RegisterAsync(
            new ChannelRelayRegistrationRequest(
                Platform: platform,
                AccessToken: accessToken,
                WebhookBaseUrl: GetStr(args, "webhook_base_url")?.Trim() ?? string.Empty,
                ScopeId: scopeResolution.ScopeId!,
                Label: GetStr(args, "label")?.Trim() ?? string.Empty,
                NyxProviderSlug: GetStr(args, "nyx_provider_slug")?.Trim() ?? string.Empty,
                Lark: new NyxChannelLarkCredentials(
                    AppId: ResolveCredential(args, credentials, platform, "app_id"),
                    AppSecret: ResolveCredential(args, credentials, platform, "app_secret"),
                    VerificationToken: ResolveCredential(args, credentials, platform, "verification_token"),
                    EncryptKey: ResolveCredential(args, credentials, platform, "encrypt_key")),
                Credentials: credentials,
                DefaultSkillName: GetStr(args, "default_skill_name")?.Trim() ?? string.Empty),
            ct);

        return SerializeRegistrationPayload(
            status: result.Status,
            platform: result.Platform,
            registrationId: result.RegistrationId ?? string.Empty,
            nyxProviderSlug: ResolveNyxProviderSlug(args, result.Platform),
            nyxChannelBotId: result.NyxChannelBotId ?? string.Empty,
            nyxAgentApiKeyId: result.NyxAgentApiKeyId ?? string.Empty,
            nyxConversationRouteId: result.NyxConversationRouteId ?? string.Empty,
            relayCallbackUrl: result.RelayCallbackUrl ?? string.Empty,
            webhookUrl: result.WebhookUrl ?? string.Empty,
            error: result.Error ?? string.Empty,
            note: result.Note ?? string.Empty);
    }

    private static IReadOnlyDictionary<string, string>? BuildCredentialsMap(JsonElement args, string platform)
    {
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);

        if (args.TryGetProperty("credentials", out var credentialsElement) &&
            credentialsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in credentialsElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = NormalizeOptional(property.Value.GetString());
                    if (value is not null)
                        credentials[property.Name] = value;
                }
            }
        }

        if (args.TryGetProperty(platform, out var platformElement) &&
            platformElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in platformElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    !credentials.ContainsKey(property.Name))
                {
                    var value = NormalizeOptional(property.Value.GetString());
                    if (value is not null)
                        credentials[property.Name] = value;
                }
            }
        }

        // Compatibility input for callers that only changed the action name. These fields are not
        // exposed in the schema because the public contract is platform-scoped credentials.
        if (string.Equals(platform, "lark", StringComparison.Ordinal))
        {
            AddTopLevelCredentialIfMissing(args, credentials, "app_id");
            AddTopLevelCredentialIfMissing(args, credentials, "app_secret");
            AddTopLevelCredentialIfMissing(args, credentials, "verification_token");
            AddTopLevelCredentialIfMissing(args, credentials, "encrypt_key");
        }
        else if (string.Equals(platform, "telegram", StringComparison.Ordinal))
        {
            AddTopLevelCredentialIfMissing(args, credentials, "bot_token");
        }

        return credentials.Count == 0 ? null : credentials;
    }

    private static void AddTopLevelCredentialIfMissing(
        JsonElement args,
        Dictionary<string, string> credentials,
        string key)
    {
        if (credentials.ContainsKey(key))
            return;

        var value = NormalizeOptional(GetStr(args, key));
        if (value is not null)
            credentials[key] = value;
    }

    private static string ResolveCredential(
        JsonElement args,
        IReadOnlyDictionary<string, string>? credentials,
        string platform,
        string key)
    {
        if (credentials is not null &&
            credentials.TryGetValue(key, out var fromCredentials) &&
            !string.IsNullOrWhiteSpace(fromCredentials))
        {
            return fromCredentials.Trim();
        }

        return GetNestedStr(args, platform, key)?.Trim() ?? string.Empty;
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
