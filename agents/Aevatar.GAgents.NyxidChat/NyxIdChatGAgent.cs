using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// NyxID chat GAgent. Extends RoleGAgent with a chat system prompt.
/// On first activation (empty state), self-initializes with the system prompt
/// so callers never need to dispatch InitializeRoleAgentEvent manually.
/// Always pins the NyxID-backed provider so requests are routed using the
/// authenticated NyxID account instead of drifting with the app default.
/// The NyxID provider itself decides whether to use a user-configured
/// chrono-llm service or fall back to the NyxID LLM gateway.
/// </summary>
public sealed class NyxIdChatGAgent : RoleGAgent
{
    private readonly SkillRegistry? _skillRegistry;
    private readonly IToolApprovalHandler? _remoteApprovalHandler;
    private readonly IUserConfigStore? _userConfigStore;

    public NyxIdChatGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        SkillRegistry? skillRegistry = null,
        IToolApprovalHandler? approvalHandler = null,
        IUserConfigStore? userConfigStore = null)
        : base(llmProviderFactory, additionalHooks, agentMiddlewares, toolMiddlewares, llmMiddlewares, toolSources,
               approvalHandler: new YieldApprovalHandler())
    {
        _skillRegistry = skillRegistry;
        _remoteApprovalHandler = approvalHandler;
        _userConfigStore = userConfigStore;
    }

    /// <summary>Provides the NyxID remote handler for approval timeout escalation.</summary>
    protected override IToolApprovalHandler? ResolveRemoteApprovalHandler() => _remoteApprovalHandler;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(State.RoleName))
        {
            await PersistDomainEventAsync(BuildInitializeRoleAgentEvent(NyxIdChatServiceDefaults.DisplayName));
        }
        else if (RequiresNyxIdProviderMigration())
        {
            await PersistDomainEventAsync(BuildInitializeRoleAgentEvent(State.RoleName));
        }

        await base.OnActivateAsync(ct);
    }

    protected override string DecorateSystemPrompt(string basePrompt)
    {
        var prompt = basePrompt;

        // Inject relay callback URL from user's chrono-storage config
        var relayUrl = ResolveRelayCallbackUrl();
        if (!string.IsNullOrWhiteSpace(relayUrl))
        {
            prompt += $"""

## Relay Configuration (Auto-Injected)

This agent's relay callback URL is: `{relayUrl}`

When setting up channel bots, use this URL as the `callback_url` for API keys:
```
nyxid_api_keys action=create name="telegram-relay" scopes="proxy read" callback_url="{relayUrl}"
```
Then create a default conversation route linking the bot to this API key.
""";
        }

        if (_skillRegistry != null && _skillRegistry.Count > 0)
        {
            var skillSection = _skillRegistry.BuildSystemPromptSection();
            if (!string.IsNullOrEmpty(skillSection))
                prompt += "\n" + skillSection;
        }

        return prompt;
    }

    /// <summary>
    /// Resolves the relay callback URL from the user's chrono-storage config.
    /// Reads remoteRuntimeBaseUrl and appends the relay webhook path.
    /// </summary>
    private string? ResolveRelayCallbackUrl()
    {
        if (_userConfigStore == null) return null;

        try
        {
            // Synchronous wait is acceptable here — DecorateSystemPrompt is called once
            // during initialization, and the config is typically cached.
            var config = _userConfigStore.GetAsync(CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();

            var baseUrl = UserConfigRuntime.ResolveActiveRuntimeBaseUrl(config);
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;

            return $"{baseUrl.TrimEnd('/')}/api/webhooks/nyxid-relay";
        }
        catch
        {
            return null;
        }
    }

    private bool RequiresNyxIdProviderMigration()
    {
        var overrides = State.ConfigOverrides;
        return overrides == null ||
               !overrides.HasProviderName ||
               string.IsNullOrWhiteSpace(overrides.ProviderName);
    }

    private InitializeRoleAgentEvent BuildInitializeRoleAgentEvent(string roleName)
    {
        var initializeEvent = new InitializeRoleAgentEvent
        {
            RoleName = string.IsNullOrWhiteSpace(roleName)
                ? NyxIdChatServiceDefaults.DisplayName
                : roleName.Trim(),
            ProviderName = NyxIdChatServiceDefaults.ProviderName,
            SystemPrompt = NyxIdChatSystemPrompt.Value,
            MaxToolRounds = State.ConfigOverrides?.HasMaxToolRounds == true &&
                            State.ConfigOverrides.MaxToolRounds > 0
                ? State.ConfigOverrides.MaxToolRounds
                : 0,
            EventModules = State.EventModules ?? string.Empty,
            EventRoutes = State.EventRoutes ?? string.Empty,
        };

        var overrides = State.ConfigOverrides;
        if (overrides?.HasModel == true)
            initializeEvent.Model = overrides.Model;

        if (overrides?.HasTemperature == true)
            initializeEvent.Temperature = overrides.Temperature;

        if (overrides?.HasMaxTokens == true && overrides.MaxTokens > 0)
            initializeEvent.MaxTokens = overrides.MaxTokens;

        if (overrides?.HasMaxHistoryMessages == true && overrides.MaxHistoryMessages > 0)
            initializeEvent.MaxHistoryMessages = overrides.MaxHistoryMessages;

        if (overrides?.HasStreamBufferCapacity == true && overrides.StreamBufferCapacity > 0)
            initializeEvent.StreamBufferCapacity = overrides.StreamBufferCapacity;

        return initializeEvent;
    }
}
