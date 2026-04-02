using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Foundation.Abstractions.Attributes;

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
    private readonly NyxIdRelayOptions? _relayOptions;

    public NyxIdChatGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        SkillRegistry? skillRegistry = null,
        IToolApprovalHandler? approvalHandler = null,
        NyxIdRelayOptions? relayOptions = null)
        : base(llmProviderFactory, additionalHooks, agentMiddlewares, toolMiddlewares, llmMiddlewares, toolSources,
               approvalHandler: new YieldApprovalHandler())
    {
        _skillRegistry = skillRegistry;
        // Keep reference to the DI-injected remote handler (NyxIdToolApprovalHandler)
        // for timeout escalation in HandleToolApprovalTimeout.
        _remoteApprovalHandler = approvalHandler;
        _relayOptions = relayOptions;
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

    /// <summary>
    /// 装饰系统 prompt：追加 SkillRegistry 中的可用技能列表。
    /// </summary>
    protected override string DecorateSystemPrompt(string basePrompt)
    {
        var prompt = basePrompt;

        // Inject relay callback URL so the agent can auto-configure channel bots
        if (!string.IsNullOrWhiteSpace(_relayOptions?.RelayCallbackUrl))
        {
            prompt += $"""

## Relay Configuration (Auto-Injected)

This agent's relay callback URL is: `{_relayOptions.RelayCallbackUrl}`

When setting up channel bots, use this URL as the `callback_url` for API keys. You can create an API key with this callback automatically:
```
nyxid_api_keys action=create name="telegram-relay" scopes="proxy read" callback_url="{_relayOptions.RelayCallbackUrl}"
```
Then create a conversation route linking the bot to this API key.
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
            // 0 = use ChatRuntime default (40). User config can override.
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
