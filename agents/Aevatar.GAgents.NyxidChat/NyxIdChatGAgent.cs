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

    public NyxIdChatGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        SkillRegistry? skillRegistry = null,
        IToolApprovalHandler? approvalHandler = null)
        : base(llmProviderFactory, additionalHooks, agentMiddlewares, toolMiddlewares, llmMiddlewares, toolSources,
               approvalHandler: new YieldApprovalHandler())
    {
        _skillRegistry = skillRegistry;
        _remoteApprovalHandler = approvalHandler;
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

        // Inject channel runtime callback base URL
        prompt += """

## Channel Runtime Configuration (Auto-Injected)

Aevatar's Nyx relay callback URL is: `https://aevatar-console-backend-api.aevatar.ai/api/webhooks/nyxid-relay`

When registering channel bots, use `channel_registrations` tool (NOT `nyxid_channel_bots`).
For Lark, use `channel_registrations action=register_lark_via_nyx`.
The Lark developer console callback URL must point to the Nyx webhook URL returned by that tool, not to an Aevatar `/api/channels/lark/callback/...` URL.
For proactive Lark chat discovery and sends, prefer `lark_chats_lookup` and `lark_messages_send` over generic `nyxid_proxy_execute`.
""";

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
