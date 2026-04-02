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
               approvalHandler: ComposeApprovalHandler(approvalHandler))
    {
        _skillRegistry = skillRegistry;
    }

    /// <summary>
    /// Compose the approval handler chain.
    /// Currently goes directly to NyxID remote approval (Telegram / mobile app, 45s poll)
    /// because local chat-based approval is blocked by the actor single-thread model:
    /// the grain cannot process ToolApprovalDecisionEvent while HandleChatRequest
    /// holds the write scope, causing an inevitable 15s deadlock-timeout.
    ///
    /// TODO: Once the actor model supports reentrant event handling for approval decisions
    /// (e.g. via a separate approval inbox or cooperative scheduling), compose as:
    ///   new PriorityApprovalHandler(new LocalApprovalHandler(), remoteHandler)
    /// to enable local-first + NyxID-remote-fallback.
    /// </summary>
    private static IToolApprovalHandler? ComposeApprovalHandler(IToolApprovalHandler? remoteHandler)
    {
        // Remote handler (NyxIdToolApprovalHandler) is injected from DI.
        // Local handler is skipped until the reentrancy issue is resolved.
        return remoteHandler;
    }

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
        if (_skillRegistry == null || _skillRegistry.Count == 0)
            return basePrompt;

        var skillSection = _skillRegistry.BuildSystemPromptSection();
        if (string.IsNullOrEmpty(skillSection))
            return basePrompt;

        return basePrompt + "\n" + skillSection;
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
