using System.Text;
using Aevatar.AI.Abstractions;
using Google.Protobuf;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Prompting;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using LlmChatFileRef = Aevatar.AI.Abstractions.LLMProviders.ChatFileRef;
using LlmChatFileSourceKind = Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind;
using FileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;

namespace Aevatar.GAgents.NyxidChat;

// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
public sealed class NyxIdConversationReplyGenerator : IAgentRunStepConversationReplyGenerator
{
    private const int MaxToolRounds = 40;
    // R1: never put the full accumulated group history into the prompt. Cap the PRIOR conversation
    // history to at most the most-recent N replayable entries on every round. Kept separate from the
    // working-set cap below so a long in-turn tool loop (assistant tool calls + tool results) is not
    // truncated mid-turn.
    private const int MaxRecentPriorHistoryMessages = 10;
    // Working-set ceiling for the ChatHistory during a turn (prior ≤10 + the live turn's growth).
    private const int MaxWorkingSetMessages = 200;
    private const int MaxAttachmentMaterializationBytes = 10 * 1024 * 1024;
    private const int MaxInlineImageBytes = 10 * 1024 * 1024;
    private const int MaxInlineDocumentBytes = 10 * 1024 * 1024;
    private const int MaxInlineDocumentTextChars = 20_000;

    // Appended to the system prompt when the unbound-sender gate detaches the tool
    // surface for a channel turn. The kernel prompt documents the deployment's tools
    // unconditionally, so the model must be told the honest, recoverable reason no
    // tool is attached — otherwise it reports the capability itself as missing.
    // Wording is tool-name-agnostic by design (CLAUDE.md: no per-skill hardcoding);
    // /init is the channel binding bootstrap the slash path already prompts for.
    private const string UnboundSenderToolsDisabledNotice =
        "## Tools disabled for this turn\n" +
        "No tools are attached to this turn: the sender's identity is not bound, and " +
        "channel tool execution requires a bound identity. Any tool or capability " +
        "documentation above describes tools you cannot invoke right now. Do not claim " +
        "a capability is missing from this deployment, and do not claim any action was " +
        "performed. If the request needs a tool, tell the user tool execution is " +
        "disabled for this turn because their identity is not bound, and that sending " +
        "/init in this chat starts the binding that enables tools.";

    // Appended instead of the unbound notice when a bound sender's attempt failed and
    // the reply is retried on the bot owner's configuration with tools stripped — the
    // same prompt/tool-surface honesty gap, different recoverable reason.
    private const string DegradedTurnToolsDisabledNotice =
        "## Tools disabled for this turn\n" +
        "No tools are attached to this turn: the sender-scoped attempt failed and this " +
        "reply is a degraded retry on the bot owner's configuration without tools. Any " +
        "tool or capability documentation above describes tools you cannot invoke right " +
        "now. Do not claim a capability is missing from this deployment, and do not claim " +
        "any action was performed. If the request needs a tool, tell the user this turn " +
        "ran degraded without tools and ask them to retry shortly.";

    // Appended when the turn's materialized tool catalog is restricted to zero
    // tools while prompt layers still document capabilities — the same honesty
    // gap as the notices above, reached through the catalog path. Without it
    // the model writes tool-call syntax into the visible reply as plain text.
    private const string RestrictedEmptyCatalogNotice =
        "## Tools disabled for this turn\n" +
        "No tools are attached to this turn: the turn tool catalog restricted this " +
        "request to zero tools. Any tool or capability documentation above describes " +
        "tools you cannot invoke right now. Never write tool-call syntax into your " +
        "reply as text. Do not claim a capability is missing from this deployment, " +
        "and do not claim any action was performed. If the request needs a tool, say " +
        "plainly that no tools are available in this turn and ask the user to retry.";

    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly IReadOnlyList<IAgentToolSource> _toolSources;
    private readonly IReadOnlyList<IAgentToolSource> _nyxIdChatToolSources;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly IAgentToolExecutionPort? _toolExecutionPort;
    private readonly LocalSkillCatalog? _localSkillCatalog;
    private readonly IRemoteSkillFetcher? _remoteSkillFetcher;
    private readonly IRemoteSkillAccessTokenResolver? _remoteSkillAccessTokenResolver;
    private readonly global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdUserLlmPreferencesStore? _preferencesStore;
    private readonly IUserMemoryPromptContextProvider? _userMemoryPromptContextProvider;
    private readonly ILarkNyxClient? _larkClient;
    private readonly IFileArtifactIngressPort? _fileIngressPort;
    private readonly IFileArtifactReadPort? _fileArtifactReadPort;
    private readonly ILarkOutboundClientFactory? _larkOutboundClientFactory;
    private readonly ISystemSkillOverlayProvider? _overlayProvider;
    private readonly IBuiltInPromptFloorProvider _builtInPromptFloorProvider;
    private readonly IAgentToolDiscoveryService _toolDiscoveryService;
    private readonly ContentArtifactConversationPromptLayerMaterializer? _contentArtifactPromptLayerMaterializer;
    private readonly ILogger<NyxIdConversationReplyGenerator> _logger;

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    private sealed record EffectiveReplyPlan(
        IReadOnlyDictionary<string, string> Primary,
        LLMControlContext PrimaryControl,
        AgentToolExecutionContext? PrimaryToolContext,
        IReadOnlyDictionary<string, string>? OwnerFallback,
        LLMControlContext? OwnerFallbackControl,
        AgentToolExecutionContext? OwnerFallbackToolContext,
        bool DisableTools);

    private sealed record SenderPreferenceApplication(
        bool ModelApplied,
        bool RouteApplied,
        bool MaxToolRoundsApplied)
    {
        public bool AnyApplied => ModelApplied || RouteApplied || MaxToolRoundsApplied;
    }

    private sealed record SenderPreferenceResult(LLMControlContext Control, SenderPreferenceApplication Application);

    public NyxIdConversationReplyGenerator(
        ILLMProviderFactory llmProviderFactory,
        IBuiltInPromptFloorProvider builtInPromptFloorProvider,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        LocalSkillCatalog? localSkillCatalog = null,
        IRemoteSkillFetcher? remoteSkillFetcher = null,
        global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions = null,
        INyxIdUserLlmPreferencesStore? preferencesStore = null,
        IUserMemoryPromptContextProvider? userMemoryPromptContextProvider = null,
        ILarkNyxClient? larkClient = null,
        IFileArtifactIngressPort? fileIngressPort = null,
        IFileArtifactReadPort? fileArtifactReadPort = null,
        ILogger<NyxIdConversationReplyGenerator>? logger = null,
        ISystemSkillOverlayProvider? overlayProvider = null,
        ILarkOutboundClientFactory? larkOutboundClientFactory = null,
        IAgentToolExecutionPort? toolExecutionPort = null,
        IRemoteSkillAccessTokenResolver? remoteSkillAccessTokenResolver = null,
        IEnumerable<IAgentToolSource>? nyxIdChatToolSources = null,
        IAgentToolDiscoveryService? toolDiscoveryService = null,
        IContentArtifactQueryPort? contentArtifactQueryPort = null)
    {
        _llmProviderFactory = llmProviderFactory ?? throw new ArgumentNullException(nameof(llmProviderFactory));
        _toolSources = (toolSources ?? []).ToArray();
        _nyxIdChatToolSources = (nyxIdChatToolSources ?? []).ToArray();
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
        _toolExecutionPort = toolExecutionPort;
        _localSkillCatalog = localSkillCatalog;
        _remoteSkillFetcher = remoteSkillFetcher;
        _remoteSkillAccessTokenResolver = remoteSkillAccessTokenResolver;
        _relayOptions = relayOptions;
        _preferencesStore = preferencesStore;
        _userMemoryPromptContextProvider = userMemoryPromptContextProvider;
        _larkClient = larkClient;
        _fileIngressPort = fileIngressPort;
        _fileArtifactReadPort = fileArtifactReadPort;
        _larkOutboundClientFactory = larkOutboundClientFactory;
        _overlayProvider = overlayProvider;
        _builtInPromptFloorProvider = builtInPromptFloorProvider ??
                                      throw new ArgumentNullException(nameof(builtInPromptFloorProvider));
        _toolDiscoveryService = toolDiscoveryService ?? AgentToolDiscoveryService.Instance;
        _contentArtifactPromptLayerMaterializer = contentArtifactQueryPort is null
            ? null
            : new ContentArtifactConversationPromptLayerMaterializer(contentArtifactQueryPort);
        _logger = logger ?? NullLogger<NyxIdConversationReplyGenerator>.Instance;
    }

    public async Task<ConversationReplyResult> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        IStreamingReplySink? streamingSink,
        CancellationToken ct) =>
        await GenerateReplyAsync(activity, metadata, llmControl: null, toolContext: null, streamingSink, ct)
            .ConfigureAwait(false);

    public async Task<ConversationReplyResult> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IStreamingReplySink? streamingSink,
        CancellationToken ct)
    {
        return await GenerateReplyAsync(
                activity,
                metadata,
                llmControl,
                toolContext,
                priorHistory: null,
                streamingSink,
                ct)
            .ConfigureAwait(false);
    }

    public async Task<ConversationReplyResult> GenerateReplyAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IReadOnlyList<ConversationHistoryEntry>? priorHistory,
        IStreamingReplySink? streamingSink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(metadata);

        // Emit a placeholder immediately so the user sees a message within the outbound RTT,
        // regardless of LLM cold-start, router selection, or tool-call latency before the
        // first real delta. The first real delta overwrites this placeholder via edit-in-place;
        // if no delta ever arrives (tool-only or empty turn), the caller's FinalizeAsync edits
        // the placeholder to the final text. Disabled by setting the option to empty/whitespace.
        if (streamingSink is not null)
        {
            var skillRecoveryStatus = BuildSkillRecoveryStreamingStatus(toolContext);
            if (!string.IsNullOrWhiteSpace(skillRecoveryStatus))
            {
                await streamingSink.OnDeltaAsync(skillRecoveryStatus, ct);
            }
            else
            {
                var placeholder = _relayOptions?.StreamingPlaceholderText;
                if (!string.IsNullOrWhiteSpace(placeholder))
                    await streamingSink.OnDeltaAsync(placeholder, ct);
            }
        }

        var replyPlan = await BuildEffectiveReplyPlanAsync(metadata, llmControl, toolContext, ct);
        var isChannelTurn = IsChannelRelayTurn(toolContext);
        var primaryDiscoveryContext = BuildEffectiveToolContext(
            replyPlan.Primary,
            replyPlan.PrimaryControl,
            replyPlan.PrimaryToolContext);
        var primaryTools = await BuildTurnToolsAsync(
            replyPlan.DisableTools,
            isChannelTurn,
            primaryDiscoveryContext,
            ct);

        try
        {
            return await GenerateWithMetadataAsync(
                    activity,
                    replyPlan.Primary,
                    replyPlan.PrimaryControl,
                    replyPlan.PrimaryToolContext,
                    priorHistory,
                    primaryTools,
                    systemPromptSuffix: replyPlan.DisableTools ? UnboundSenderToolsDisabledNotice : null,
                    streamingSink,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (replyPlan.OwnerFallback is not null && LlmOwnerFallbackPolicy.IsRetryable(ex))
        {
            _logger.LogWarning(
                ex,
                "Sender LLM request failed; retrying with bot owner LLM config and no tools. activity={ActivityId}",
                activity.Id);

            var fallbackTools = await BuildTurnToolsAsync(
                disableTools: true,
                isChannelTurn,
                discoveryContext: null,
                ct);
            return await GenerateWithMetadataAsync(
                    activity,
                    replyPlan.OwnerFallback,
                    replyPlan.OwnerFallbackControl ?? llmControl ?? LLMControlContext.Empty,
                    replyPlan.OwnerFallbackToolContext,
                    priorHistory,
                    fallbackTools,
                    systemPromptSuffix: replyPlan.DisableTools
                        ? UnboundSenderToolsDisabledNotice
                        : DegradedTurnToolsDisabledNotice,
                    streamingSink,
                    ct)
                .ConfigureAwait(false);
        }
    }

    public async Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IReadOnlyList<ConversationHistoryEntry>? priorHistory,
        bool forceDisableTools,
        CancellationToken ct)
    {
        return await BuildStepPlanCoreAsync(
                activity,
                metadata,
                llmControl,
                toolContext,
                priorHistory,
                attachmentContext: null,
                forceDisableTools,
                turnCatalog: null,
                ct)
            .ConfigureAwait(false);
    }

    async Task<AgentRunReplyStepPlan> IAgentRunStepConversationReplyGenerator.BuildStepPlanAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IReadOnlyList<ConversationHistoryEntry>? priorHistory,
        ChatAttachmentInputContext? attachmentContext,
        bool forceDisableTools,
        CancellationToken ct,
        AgentTurnToolCatalog? turnCatalog) =>
        await BuildStepPlanCoreAsync(
                activity,
                metadata,
                llmControl,
                toolContext,
                priorHistory,
                attachmentContext,
                forceDisableTools,
                turnCatalog,
                ct)
            .ConfigureAwait(false);

    private async Task<AgentRunReplyStepPlan> BuildStepPlanCoreAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IReadOnlyList<ConversationHistoryEntry>? priorHistory,
        ChatAttachmentInputContext? attachmentContext,
        bool forceDisableTools,
        AgentTurnToolCatalog? turnCatalog,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(metadata);

        var replyPlan = await BuildEffectiveReplyPlanAsync(metadata, llmControl, toolContext, ct);
        var provider = ResolveProvider();
        var disableTools = forceDisableTools || replyPlan.DisableTools;
        var externalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(replyPlan.Primary);
        var effectiveToolContext = BuildEffectiveToolContext(
            replyPlan.Primary,
            replyPlan.PrimaryControl,
            replyPlan.PrimaryToolContext);
        var isChannelRelayTurn = IsChannelRelayTurn(toolContext);
        var effectiveTurnCatalog = turnCatalog;
        var tools = effectiveTurnCatalog is null
            ? await BuildTurnToolsAsync(
                disableTools,
                isChannelRelayTurn,
                effectiveToolContext,
                ct)
            : BuildProfileTools(disableTools, effectiveTurnCatalog);
        var input = await BuildUserInputPartsAsync(
                activity,
                provider,
                attachmentContext,
                ct)
            .ConfigureAwait(false);
        var inputFileRefs = CollectInputFileRefs(input.Parts);
        var conversationLayer = await MaterializeConversationContextLayerAsync(
                attachmentContext,
                effectiveToolContext,
                ct)
            .ConfigureAwait(false);
        effectiveToolContext = WithInputFileRefs(effectiveToolContext, inputFileRefs)!;
        var ownerFallbackToolContext = WithInputFileRefs(replyPlan.OwnerFallbackToolContext, inputFileRefs);
        LogChannelLlmToolPlan(
            "actor-step",
            isChannelRelayTurn,
            forceDisableTools,
            replyPlan.DisableTools,
            disableTools,
            effectiveTurnCatalog,
            effectiveToolContext,
            inputFileRefs,
            tools);

        var runtime = BuildRuntime(
            activity,
            replyPlan.PrimaryControl,
            effectiveToolContext,
            externalMetadata,
            tools,
            input.AttachmentVisibilityInstruction,
            conversationLayer);

        // The unbound-sender gate (issue #1318) detaches the entire tool surface while
        // the kernel prompt still documents those tools; without this override the
        // model reports the capability as missing instead of the actual, recoverable
        // reason. Keyed on the plan's own gate (not forceDisableTools): per-step rebuilds
        // discard InitialMessages, so this notice is stamped exactly once per run.
        var initialMessages = new List<ChatMessage>
        {
            ChatMessage.System(BuildSystemPrompt(
                externalMetadata,
                effectiveToolContext,
                input.AttachmentVisibilityInstruction,
                replyPlan.DisableTools
                    ? UnboundSenderToolsDisabledNotice
                    : effectiveTurnCatalog is { ExactTools.Count: 0 }
                        ? RestrictedEmptyCatalogNotice
                : null,
                effectiveTurnCatalog,
                conversationLayer)),
        };
        initialMessages.AddRange((priorHistory ?? []).Where(IsReplayableHistoryEntry).TakeLast(MaxRecentPriorHistoryMessages).Select(ToChatMessage));
        initialMessages.Add(ChatMessage.User(input.Parts, input.Text));

        return new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(effectiveTurnCatalog),
            externalMetadata,
            replyPlan.PrimaryControl,
            effectiveToolContext,
            initialMessages,
            ResolveMaxToolRounds(replyPlan.PrimaryControl),
            disableTools,
            replyPlan.OwnerFallbackControl,
            ownerFallbackToolContext);
    }

    private static ToolManager BuildProfileTools(
        bool disableTools,
        AgentTurnToolCatalog turnCatalog)
    {
        var tools = new ToolManager();
        if (!disableTools)
            tools.Register(turnCatalog.ExactTools.Values);
        return tools;
    }

    private void LogChannelLlmToolPlan(
        string surface,
        bool isChannelRelayTurn,
        bool forceDisableTools,
        bool replyPlanDisableTools,
        bool disableTools,
        AgentTurnToolCatalog? turnCatalog,
        AgentToolExecutionContext toolContext,
        IReadOnlyList<Aevatar.AI.Abstractions.ChatFileRef> inputFileRefs,
        ToolManager tools)
    {
        var isNyxIdChatTurn = IsNyxIdChatTurn(toolContext);
        if (!isChannelRelayTurn && !isNyxIdChatTurn)
            return;

        var validTools = FilterValidTools(tools) ?? [];
        _logger.LogWarning(
            "Channel LLM tool plan prepared. surface={Surface} isChannelRelayTurn={IsChannelRelayTurn} isNyxIdChatTurn={IsNyxIdChatTurn} forceDisableTools={ForceDisableTools} replyPlanDisableTools={ReplyPlanDisableTools} disableTools={DisableTools} turnCatalogPresent={TurnCatalogPresent} profileAllowedToolCount={ProfileAllowedToolCount} profileAllowedTools={ProfileAllowedTools} routeOwnedToolCount={ExactToolCount} exactTools={ExactTools} finalToolCount={FinalToolCount} finalTools={FinalTools} inputPartFileRefCount={InputPartFileRefCount} toolContextInputFileRefCount={ToolContextInputFileRefCount}",
            surface,
            isChannelRelayTurn,
            isNyxIdChatTurn,
            forceDisableTools,
            replyPlanDisableTools,
            disableTools,
            turnCatalog is not null,
            turnCatalog?.FinalAllowedToolNames.Count ?? 0,
            FormatToolNames(turnCatalog?.FinalAllowedToolNames ?? Enumerable.Empty<string>()),
            turnCatalog?.ExactTools.Count ?? 0,
            FormatToolNames(turnCatalog?.ExactTools.Values.Select(static tool => tool.Name) ?? Enumerable.Empty<string>()),
            validTools.Count,
            FormatToolNames(validTools.Select(static tool => tool.Name)),
            inputFileRefs.Count,
            toolContext.InputFileRefs.Count);
    }

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    private async Task<ToolManager> BuildTurnToolsAsync(
        bool disableTools,
        bool isChannelTurn,
        AgentToolExecutionContext? discoveryContext,
        CancellationToken ct)
    {
        var tools = new ToolManager();
        if (disableTools)
            return tools;

        using (AgentToolContextScope.Push(discoveryContext))
        {
            foreach (var tool in await DiscoverToolsAsync(isChannelTurn, discoveryContext, ct))
                tools.Register(tool);
        }

        // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
        //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
        //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
        if (!IsNyxIdChatTurn(discoveryContext) &&
            (_localSkillCatalog is not null || _remoteSkillFetcher is not null) &&
            tools.Get("use_skill") is null)
        {
            tools.Register(new UseSkillTool(
                _localSkillCatalog ?? new LocalSkillCatalog(),
                _remoteSkillFetcher,
                remoteAccessTokenResolver: _remoteSkillAccessTokenResolver));
        }

        return tools;
    }

    private static AgentToolExecutionContext BuildEffectiveToolContext(
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext control,
        AgentToolExecutionContext? baseContext)
    {
        var externalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(metadata);
        var context = baseContext ?? AgentToolExecutionContext.Empty with
        {
            ExternalMetadata = externalMetadata,
        };
        return control.ToToolContext(context);
    }

    private async Task<ConversationReplyResult> GenerateWithMetadataAsync(
        ChatActivity activity,
        IReadOnlyDictionary<string, string> effectiveMetadata,
        LLMControlContext llmControl,
        AgentToolExecutionContext? baseToolContext,
        IReadOnlyList<ConversationHistoryEntry>? priorHistory,
        ToolManager tools,
        string? systemPromptSuffix,
        IStreamingReplySink? streamingSink,
        CancellationToken ct)
    {
        var externalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(effectiveMetadata);
        var toolContext = llmControl.ToToolContext(baseToolContext ?? AgentToolExecutionContext.Empty with
        {
            ExternalMetadata = externalMetadata,
        });

        var provider = ResolveProvider();
        var input = await BuildUserInputPartsAsync(
                activity,
                provider,
                attachmentContext: null,
                ct)
            .ConfigureAwait(false);
        input = await MaterializeUserInputPartsAsync(input, ct).ConfigureAwait(false);
        var inputFileRefs = CollectInputFileRefs(input.Parts);
        toolContext = WithInputFileRefs(toolContext, inputFileRefs)!;
        LogChannelLlmToolPlan(
            "direct-reply",
            IsChannelRelayTurn(toolContext),
            forceDisableTools: false,
            replyPlanDisableTools: false,
            disableTools: false,
            turnCatalog: null,
            toolContext,
            inputFileRefs,
            tools);

        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: NyxID reply construction passed stream_buffer_capacity into ChatRuntime after the stream loop moved to Task.Run + Channel.
        //   New principle: ChatRuntime owns the async stream directly; this caller only supplies provider, tools, middleware, and request identity.
        var history = new global::Aevatar.AI.Core.Chat.ChatHistory
        {
            MaxMessages = MaxWorkingSetMessages,
        };
        history.AddRange((priorHistory ?? []).Where(IsReplayableHistoryEntry).TakeLast(MaxRecentPriorHistoryMessages).Select(ToChatMessage));
        var importedPriorCount = history.Messages.Count;
        var runtime = new ChatRuntime(
            providerFactory: ResolveProvider,
            history: history,
            toolLoop: new ToolCallLoop(
                tools,
                hooks: null,
                llmMiddlewares: _llmMiddlewares,
                toolExecutionPort: _toolExecutionPort),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages =
                [
                    ChatMessage.System(BuildSystemPrompt(
                        effectiveMetadata,
                        toolContext,
                        input.AttachmentVisibilityInstruction,
                        systemPromptSuffix)),
                ],
                Metadata = externalMetadata,
                ToolContext = toolContext,
                LlmControl = llmControl,
                RoutingContext = llmControl.ToRoutingContext(),
                Tools = FilterValidTools(tools),
            },
            agentMiddlewares: _agentMiddlewares,
            llmMiddlewares: _llmMiddlewares,
            agentId: activity.Conversation?.CanonicalKey,
            agentName: "NyxIdConversationReply",
            suppressToolCallRoundText: true);

        var output = new StringBuilder();
        // ADR-0021 §6 / canon §8 actor-edge closeout: aggregate Usage and track the last
        // FinishReason across all internal LLM rounds (tool-call loop) so the caller sees
        // exactly one closeout — the returned record — instead of relying on round-internal
        // markers that ChatRuntime currently passes through.
        ReplyTokenUsage? aggregatedUsage = null;
        string? lastFinishReason = null;
        var suppressInitialSlashCommandStatus = false;
        await foreach (var chunk in runtime.ChatStreamAsync(
                           input.Parts,
                           MaxToolRounds,
                           activity.Id,
                           llmControl,
                           toolContext,
                           turnCatalog: null,
                           externalMetadata,
                           ct))
        {
            if (chunk.Usage is { } usage)
                aggregatedUsage = SumUsage(aggregatedUsage, MapUsage(usage));
            if (!string.IsNullOrEmpty(chunk.FinishReason))
                lastFinishReason = chunk.FinishReason;

            if (string.IsNullOrEmpty(chunk.DeltaContent))
                continue;

            if (IsInitialSlashCommandStatusChunk(chunk.DeltaContent))
            {
                suppressInitialSlashCommandStatus = true;
                if (streamingSink is not null && ShouldStreamVisibleReply(chunk.DeltaContent))
                    await streamingSink.OnDeltaAsync(chunk.DeltaContent, ct);
                continue;
            }

            if (suppressInitialSlashCommandStatus && IsSlashCommandStatusSpacerChunk(chunk.DeltaContent))
                continue;

            suppressInitialSlashCommandStatus = false;
            output.Append(chunk.DeltaContent);
            if (streamingSink is not null && ShouldStreamVisibleReply(output.ToString()))
                await streamingSink.OnDeltaAsync(output.ToString(), ct);
        }

        return new ConversationReplyResult(
            Text: output.ToString(),
            Usage: aggregatedUsage,
            FinishReason: lastFinishReason,
            AppendedHistory: ExportAppendedHistory(history, importedPriorCount));
    }

    private static bool IsInitialSlashCommandStatusChunk(string content) =>
        content.StartsWith("⏳ 正在处理 `/", StringComparison.Ordinal);

    private static bool IsSlashCommandStatusSpacerChunk(string content) =>
        content.All(static ch => ch is '\r' or '\n');

    private static IReadOnlyList<ConversationHistoryEntry> ExportAppendedHistory(
        global::Aevatar.AI.Core.Chat.ChatHistory history,
        int priorCount) =>
        history.Messages
            .Skip(Math.Clamp(priorCount, 0, history.Messages.Count))
            .Select(ToConversationHistoryEntry)
            .ToArray();

    private sealed record UserInputParts(
        string Text,
        IReadOnlyList<ContentPart> Parts,
        string? AttachmentVisibilityInstruction = null);

    private static AgentToolExecutionContext? WithInputFileRefs(
        AgentToolExecutionContext? context,
        IReadOnlyList<Aevatar.AI.Abstractions.ChatFileRef> inputFileRefs)
    {
        if (context is null || inputFileRefs.Count == 0)
            return context;

        var merged = new List<Aevatar.AI.Abstractions.ChatFileRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fileRef in context.InputFileRefs.Concat(inputFileRefs))
        {
            var key = FileRefIdentityKey(fileRef);
            if (key is null || !seen.Add(key))
                continue;

            merged.Add(fileRef.Clone());
        }

        return context with { InputFileRefs = merged };
    }

    private static IReadOnlyList<Aevatar.AI.Abstractions.ChatFileRef> CollectInputFileRefs(
        IReadOnlyList<ContentPart> parts)
    {
        var refs = new List<Aevatar.AI.Abstractions.ChatFileRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (part.FileRef is null || !HasFileRefIdentity(part.FileRef))
                continue;

            var key = FileRefIdentityKey(part.FileRef);
            if (key is null || !seen.Add(key))
                continue;

            refs.Add(ToProtoChatFileRef(part.FileRef));
        }

        return refs;
    }

    private static Aevatar.AI.Abstractions.ChatFileRef ToProtoChatFileRef(LlmChatFileRef fileRef) =>
        new()
        {
            FileId = fileRef.FileId ?? string.Empty,
            ArtifactId = fileRef.ArtifactId ?? string.Empty,
            SourceKind = fileRef.SourceKind switch
            {
                LlmChatFileSourceKind.ChatInput => Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput,
                LlmChatFileSourceKind.FormUpload => Aevatar.AI.Abstractions.ChatFileSourceKind.FormUpload,
                LlmChatFileSourceKind.ConnectedServiceResource => Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
                LlmChatFileSourceKind.ExternalResource => Aevatar.AI.Abstractions.ChatFileSourceKind.ExternalResource,
                LlmChatFileSourceKind.Generated => Aevatar.AI.Abstractions.ChatFileSourceKind.Generated,
                _ => Aevatar.AI.Abstractions.ChatFileSourceKind.Unspecified,
            },
            SourceMessageId = fileRef.SourceMessageId ?? string.Empty,
            SourceResourceKey = fileRef.SourceResourceKey ?? string.Empty,
            FileName = fileRef.FileName ?? string.Empty,
            MediaType = fileRef.MediaType ?? string.Empty,
            SizeBytes = fileRef.SizeBytes,
            Sha256 = fileRef.Sha256 ?? string.Empty,
            CreatedAtUnixMs = fileRef.CreatedAtUnixMs,
            ExpiresAtUnixMs = fileRef.ExpiresAtUnixMs,
            OwnerRunId = fileRef.OwnerRunId ?? string.Empty,
            OwnerScopeId = fileRef.OwnerScopeId ?? string.Empty,
        };

    private static bool HasFileRefIdentity(LlmChatFileRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

    private static string? FileRefIdentityKey(LlmChatFileRef fileRef)
    {
        if (!string.IsNullOrWhiteSpace(fileRef.ArtifactId))
            return $"artifact:{fileRef.ArtifactId.Trim()}";

        if (!string.IsNullOrWhiteSpace(fileRef.FileId))
            return $"file:{fileRef.FileId.Trim()}";

        return null;
    }

    private static string? FileRefIdentityKey(Aevatar.AI.Abstractions.ChatFileRef fileRef)
    {
        if (!string.IsNullOrWhiteSpace(fileRef.ArtifactId))
            return $"artifact:{fileRef.ArtifactId.Trim()}";

        if (!string.IsNullOrWhiteSpace(fileRef.FileId))
            return $"file:{fileRef.FileId.Trim()}";

        return null;
    }

    private async Task<UserInputParts> BuildUserInputPartsAsync(
        ChatActivity activity,
        ILLMProvider provider,
        ChatAttachmentInputContext? attachmentContext,
        CancellationToken ct)
    {
        var text = activity.Content?.Text ?? string.Empty;
        var parts = new List<ContentPart> { ContentPart.TextPart(text) };
        var currentAttachmentCount = activity.Content?.Attachments?.Count ?? 0;
        var recentAttachmentCount = CountAttachments(attachmentContext?.RecentAttachmentActivities
            .Where(static entry => entry.Activity?.Content?.Attachments is { Count: > 0 })
            .Select(static entry => new AttachmentActivity(
                entry.Activity!,
                entry.Activity!.Content!.Attachments.Select(static attachment => attachment.Clone()).ToArray())) ?? []);
        var attachments = SelectAttachmentActivities(activity, attachmentContext).ToArray();
        if (IsLarkActivity(activity) || attachments.Any(static attachment => IsLarkActivity(attachment.Activity)))
        {
            _logger.LogWarning(
                "Channel attachment input selection prepared. activityId={ActivityId} currentAttachmentCount={CurrentAttachmentCount} recentAttachmentCount={RecentAttachmentCount} selectedAttachmentActivityCount={SelectedAttachmentActivityCount} selectedAttachmentCount={SelectedAttachmentCount}",
                activity.Id,
                currentAttachmentCount,
                recentAttachmentCount,
                attachments.Length,
                CountAttachments(attachments));
        }

        if (attachments.Length == 0)
            return new UserInputParts(text, parts);

        if (_larkClient is null && _larkOutboundClientFactory is null)
        {
            return new UserInputParts(
                text,
                parts,
                BuildAttachmentVisibilityInstruction(
                    CountAttachments(attachments),
                    "channel resource download is not available in this runtime"));
        }

        var token = NormalizeOptional(attachmentContext?.UserAccessToken)
                    ?? NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken);
        if (token is null)
        {
            return new UserInputParts(
                text,
                parts,
                BuildAttachmentVisibilityInstruction(
                    CountAttachments(attachments),
                    "the channel user credential needed to download the attachment is unavailable"));
        }

        var unseenCount = 0;
        var imageInputUnsupportedCount = 0;
        foreach (var source in attachments)
        {
            if (!IsLarkActivity(source.Activity))
            {
                unseenCount++;
                continue;
            }

            var messageId = NormalizeOptional(source.Activity.TransportExtras?.NyxPlatformMessageId);
            if (messageId is null)
            {
                unseenCount += source.Attachments.Count;
                continue;
            }

            var larkClient = ResolveLarkResourceDownloadClient(source.Activity, out var providerSlug);
            if (larkClient is null)
            {
                _logger.LogWarning(
                    "Lark resource download client is unavailable for chat LLM input: provider={ProviderSlug} messageId={MessageId}",
                    providerSlug,
                    messageId);
                unseenCount += source.Attachments.Count;
                continue;
            }

            foreach (var attachment in source.Attachments)
            {
                if (IsLarkPdfInputAttachment(attachment))
                {
                    if (await TryAddLarkPdfTextPartAsync(
                            parts,
                            larkClient,
                            token,
                            providerSlug,
                            messageId,
                            attachment,
                            ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    unseenCount++;
                    continue;
                }

                if (IsLarkTextInputAttachment(attachment))
                {
                    if (await TryAddLarkTextFilePartAsync(
                            parts,
                            larkClient,
                            token,
                            providerSlug,
                            messageId,
                            attachment,
                            ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    unseenCount++;
                    continue;
                }

                if (!IsLarkImageInputAttachment(attachment))
                {
                    _logger.LogDebug(
                        "Skipping unsupported Lark attachment for chat LLM input: provider={ProviderSlug} messageId={MessageId} attachmentKind={AttachmentKind} contentType={ContentType} name={Name}",
                        providerSlug,
                        messageId,
                        attachment.Kind,
                        attachment.ContentType,
                        attachment.Name);
                    unseenCount++;
                    continue;
                }

                if (!provider.Capabilities.SupportsInput(ContentPartKind.Image))
                {
                    _logger.LogDebug(
                        "Skipping Lark image attachment because selected LLM route does not support image input: provider={ProviderSlug} messageId={MessageId} attachmentKind={AttachmentKind} contentType={ContentType} name={Name}",
                        providerSlug,
                        messageId,
                        attachment.Kind,
                        attachment.ContentType,
                        attachment.Name);
                    unseenCount++;
                    imageInputUnsupportedCount++;
                    continue;
                }

                if (attachment.SizeBytes > MaxInlineImageBytes)
                {
                    _logger.LogWarning(
                        "Skipping oversized Lark image attachment for chat LLM input: provider={ProviderSlug} messageId={MessageId} attachmentKind={AttachmentKind} contentType={ContentType} name={Name} sizeBytes={SizeBytes} maxBytes={MaxBytes}",
                        providerSlug,
                        messageId,
                        attachment.Kind,
                        attachment.ContentType,
                        attachment.Name,
                        attachment.SizeBytes,
                        MaxInlineImageBytes);
                    unseenCount++;
                    continue;
                }

                var resourceKey = LarkAttachmentResourceKeys.Normalize(attachment.AttachmentId);
                if (resourceKey is null)
                {
                    _logger.LogWarning(
                        "Skipping Lark image attachment without resource key for chat LLM input: provider={ProviderSlug} messageId={MessageId} attachmentKind={AttachmentKind} contentType={ContentType} name={Name}",
                        providerSlug,
                        messageId,
                        attachment.Kind,
                        attachment.ContentType,
                        attachment.Name);
                    unseenCount++;
                    continue;
                }

                var resourceKind = ToLarkMessageResourceKind(attachment);
                LarkMessageResourceDownloadResult downloaded;
                try
                {
                    downloaded = await larkClient.DownloadMessageResourceAsync(
                            token,
                            new LarkMessageResourceDownloadRequest(
                                messageId,
                                resourceKey,
                                resourceKind),
                            ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to download Lark image attachment for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} resourceKind={ResourceKind} attachmentKind={AttachmentKind} contentType={ContentType} name={Name}",
                        providerSlug,
                        messageId,
                        resourceKey,
                        resourceKind,
                        attachment.Kind,
                        attachment.ContentType,
                        attachment.Name);
                    unseenCount++;
                    continue;
                }

                var mediaType = ResolveDownloadedImageMediaType(
                    downloaded.ContentType,
                    attachment.ContentType,
                    downloaded.FileName,
                    attachment.Name);
                if (!downloaded.Succeeded ||
                    downloaded.Content.Length == 0 ||
                    downloaded.Content.Length > MaxInlineImageBytes ||
                    mediaType is null)
                {
                    _logger.LogWarning(
                        "Lark image attachment download was not usable for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} resourceKind={ResourceKind} attachmentKind={AttachmentKind} contentType={ContentType} downloadedContentType={DownloadedContentType} name={Name} downloadedName={DownloadedName} status={Status} detail={Detail}",
                        providerSlug,
                        messageId,
                        resourceKey,
                        resourceKind,
                        attachment.Kind,
                        attachment.ContentType,
                        downloaded.ContentType,
                        attachment.Name,
                        downloaded.FileName,
                        downloaded.HttpStatus,
                        downloaded.Detail);
                    unseenCount++;
                    continue;
                }

                _logger.LogDebug(
                    "Downloaded Lark image attachment for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} resourceKind={ResourceKind} attachmentKind={AttachmentKind} mediaType={MediaType} name={Name} sizeBytes={SizeBytes}",
                    providerSlug,
                    messageId,
                    resourceKey,
                    resourceKind,
                    attachment.Kind,
                    mediaType,
                    NormalizeOptional(downloaded.FileName) ?? NormalizeOptional(attachment.Name),
                    downloaded.Content.Length);

                if (_fileIngressPort is null)
                {
                    _logger.LogWarning(
                        "File ingress port is unavailable for Lark image attachment chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} resourceKind={ResourceKind}",
                        providerSlug,
                        messageId,
                        resourceKey,
                        resourceKind);
                    unseenCount++;
                    continue;
                }

                var fileName = NormalizeOptional(downloaded.FileName) ?? NormalizeOptional(attachment.Name);
                FileArtifactIngressResult ingressResult;
                try
                {
                    ingressResult = await _fileIngressPort.IngestAsync(
                            new FileArtifactIngressRequest(
                                downloaded.Content,
                                FileArtifactSourceKind.ChatInput,
                                SourceMessageId: messageId,
                                SourceResourceKey: resourceKey,
                                FileName: fileName,
                                MediaType: mediaType),
                            ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to ingest Lark image attachment for chat LLM input: messageId={MessageId} resourceKey={ResourceKey}",
                        messageId,
                        resourceKey);
                    unseenCount++;
                    continue;
                }

                parts.Add(ContentPart.ImageFileRefPart(
                    ToChatFileRef(ingressResult.FileRef),
                    mediaType,
                    fileName));
            }
        }

        var unseenReason = unseenCount > 0 && unseenCount == imageInputUnsupportedCount
            ? "selected LLM route does not support image input"
            : "one or more attachments could not be converted to LLM input";
        var instruction = unseenCount > 0
            ? BuildAttachmentVisibilityInstruction(unseenCount, unseenReason)
            : null;
        if (IsLarkActivity(activity) || attachments.Any(static attachment => IsLarkActivity(attachment.Activity)))
        {
            _logger.LogWarning(
                "Channel attachment input processing completed. activityId={ActivityId} selectedAttachmentCount={SelectedAttachmentCount} outputPartCount={OutputPartCount} outputFileRefPartCount={OutputFileRefPartCount} unseenAttachmentCount={UnseenAttachmentCount} imageInputUnsupportedCount={ImageInputUnsupportedCount}",
                activity.Id,
                CountAttachments(attachments),
                parts.Count,
                parts.Count(static part => part.FileRef is not null),
                unseenCount,
                imageInputUnsupportedCount);
        }

        return new UserInputParts(text, parts, instruction);
    }

    private async Task<UserInputParts> MaterializeUserInputPartsAsync(UserInputParts input, CancellationToken ct)
    {
        if (!input.Parts.Any(static part => part.FileRef is not null))
            return input;

        var materialized = await MaterializeFileRefPartsAsync(input.Parts, _fileArtifactReadPort, ct)
            .ConfigureAwait(false);
        return input with { Parts = materialized };
    }

    private async Task<bool> TryAddLarkPdfTextPartAsync(
        List<ContentPart> parts,
        ILarkNyxClient larkClient,
        string token,
        string? providerSlug,
        string messageId,
        AttachmentRef attachment,
        CancellationToken ct)
    {
        if (attachment.SizeBytes > MaxInlineDocumentBytes)
        {
            _logger.LogWarning(
                "Skipping oversized Lark PDF attachment for chat LLM input: provider={ProviderSlug} messageId={MessageId} contentType={ContentType} name={Name} sizeBytes={SizeBytes} maxBytes={MaxBytes}",
                providerSlug,
                messageId,
                attachment.ContentType,
                attachment.Name,
                attachment.SizeBytes,
                MaxInlineDocumentBytes);
            return false;
        }

        var resourceKey = LarkAttachmentResourceKeys.Normalize(attachment.AttachmentId);
        if (resourceKey is null)
        {
            _logger.LogWarning(
                "Skipping Lark PDF attachment without resource key for chat LLM input: provider={ProviderSlug} messageId={MessageId} contentType={ContentType} name={Name}",
                providerSlug,
                messageId,
                attachment.ContentType,
                attachment.Name);
            return false;
        }

        var fileRef = await TryIngestLarkFileAttachmentAsync(
                larkClient,
                token,
                providerSlug,
                messageId,
                attachment,
                resourceKey,
                LarkMessageResourceKind.File,
                ResolveDownloadedPdfMediaType,
                MaxInlineDocumentBytes,
                "PDF",
                ct)
            .ConfigureAwait(false);
        if (fileRef is null)
            return false;

        var content = await TryReadLarkFileArtifactBytesAsync(
                fileRef,
                MaxInlineDocumentBytes,
                "PDF",
                ct)
            .ConfigureAwait(false);
        if (content is null)
            return false;

        string extractedText;
        bool truncated;
        try
        {
            (extractedText, truncated) = ExtractPdfText(content, MaxInlineDocumentTextChars);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to extract Lark PDF attachment text for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} name={Name}",
                providerSlug,
                messageId,
                resourceKey,
                NormalizeOptional(fileRef.FileName) ?? NormalizeOptional(attachment.Name));
            return false;
        }

        if (string.IsNullOrWhiteSpace(extractedText))
        {
            _logger.LogWarning(
                "Lark PDF attachment produced no extractable text for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} name={Name}",
                providerSlug,
                messageId,
                resourceKey,
                NormalizeOptional(fileRef.FileName) ?? NormalizeOptional(attachment.Name));
            return false;
        }

        parts.Add(BuildDocumentFileRefPart(fileRef, attachment.Name));
        return true;
    }

    private async Task<bool> TryAddLarkTextFilePartAsync(
        List<ContentPart> parts,
        ILarkNyxClient larkClient,
        string token,
        string? providerSlug,
        string messageId,
        AttachmentRef attachment,
        CancellationToken ct)
    {
        if (attachment.SizeBytes > MaxInlineDocumentBytes)
        {
            _logger.LogWarning(
                "Skipping oversized Lark text attachment for chat LLM input: provider={ProviderSlug} messageId={MessageId} contentType={ContentType} name={Name} sizeBytes={SizeBytes} maxBytes={MaxBytes}",
                providerSlug,
                messageId,
                attachment.ContentType,
                attachment.Name,
                attachment.SizeBytes,
                MaxInlineDocumentBytes);
            return false;
        }

        var resourceKey = LarkAttachmentResourceKeys.Normalize(attachment.AttachmentId);
        if (resourceKey is null)
        {
            _logger.LogWarning(
                "Skipping Lark text attachment without resource key for chat LLM input: provider={ProviderSlug} messageId={MessageId} contentType={ContentType} name={Name}",
                providerSlug,
                messageId,
                attachment.ContentType,
                attachment.Name);
            return false;
        }

        var fileRef = await TryIngestLarkFileAttachmentAsync(
                larkClient,
                token,
                providerSlug,
                messageId,
                attachment,
                resourceKey,
                LarkMessageResourceKind.File,
                ResolveDownloadedTextMediaType,
                MaxInlineDocumentBytes,
                "text",
                ct)
            .ConfigureAwait(false);
        if (fileRef is null)
            return false;

        var content = await TryReadLarkFileArtifactBytesAsync(
                fileRef,
                MaxInlineDocumentBytes,
                "text",
                ct)
            .ConfigureAwait(false);
        if (content is null)
            return false;

        var (fileText, truncated) = ExtractUtf8Text(content, MaxInlineDocumentTextChars);
        if (string.IsNullOrWhiteSpace(fileText))
        {
            _logger.LogWarning(
                "Lark text attachment produced no usable text for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} name={Name}",
                providerSlug,
                messageId,
                resourceKey,
                NormalizeOptional(fileRef.FileName) ?? NormalizeOptional(attachment.Name));
            return false;
        }

        parts.Add(BuildDocumentFileRefPart(fileRef, attachment.Name));
        return true;
    }

    private async Task<FileArtifactRef?> TryIngestLarkFileAttachmentAsync(
        ILarkNyxClient larkClient,
        string token,
        string? providerSlug,
        string messageId,
        AttachmentRef attachment,
        string resourceKey,
        LarkMessageResourceKind resourceKind,
        Func<string?, string?, string?, string?, string?> resolveMediaType,
        int maxBytes,
        string attachmentLabel,
        CancellationToken ct)
    {
        LarkMessageResourceDownloadResult downloaded;
        try
        {
            downloaded = await larkClient.DownloadMessageResourceAsync(
                    token,
                    new LarkMessageResourceDownloadRequest(
                        messageId,
                        resourceKey,
                        resourceKind),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to download Lark {AttachmentLabel} attachment for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} contentType={ContentType} name={Name}",
                attachmentLabel,
                providerSlug,
                messageId,
                resourceKey,
                attachment.ContentType,
                attachment.Name);
            return null;
        }

        var mediaType = resolveMediaType(
            downloaded.ContentType,
            attachment.ContentType,
            downloaded.FileName,
            attachment.Name);
        if (!downloaded.Succeeded ||
            downloaded.Content.Length == 0 ||
            downloaded.Content.Length > maxBytes ||
            mediaType is null)
        {
            _logger.LogWarning(
                "Lark {AttachmentLabel} attachment download was not usable for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} contentType={ContentType} downloadedContentType={DownloadedContentType} name={Name} downloadedName={DownloadedName} status={Status} detail={Detail}",
                attachmentLabel,
                providerSlug,
                messageId,
                resourceKey,
                attachment.ContentType,
                downloaded.ContentType,
                attachment.Name,
                downloaded.FileName,
                downloaded.HttpStatus,
                downloaded.Detail);
            return null;
        }

        if (_fileIngressPort is null)
        {
            _logger.LogWarning(
                "File ingress port is unavailable for Lark {AttachmentLabel} attachment chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} resourceKind={ResourceKind}",
                attachmentLabel,
                providerSlug,
                messageId,
                resourceKey,
                resourceKind);
            return null;
        }

        var fileName = NormalizeOptional(downloaded.FileName) ?? NormalizeOptional(attachment.Name);
        try
        {
            var ingressResult = await _fileIngressPort.IngestAsync(
                    new FileArtifactIngressRequest(
                        downloaded.Content,
                        FileArtifactSourceKind.ChatInput,
                        SourceMessageId: messageId,
                        SourceResourceKey: resourceKey,
                        FileName: fileName,
                        MediaType: mediaType),
                    ct)
                .ConfigureAwait(false);
            return ingressResult.FileRef;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to ingest Lark {AttachmentLabel} attachment for chat LLM input: messageId={MessageId} resourceKey={ResourceKey}",
                attachmentLabel,
                messageId,
                resourceKey);
            return null;
        }
    }

    private static ContentPart BuildDocumentFileRefPart(FileArtifactRef fileRef, string? fallbackFileName) =>
        new()
        {
            Kind = ContentPartKind.Text,
            FileRef = ToChatFileRef(fileRef),
            MediaType = NormalizeOptional(fileRef.MediaType),
            Name = NormalizeOptional(fileRef.FileName) ?? NormalizeOptional(fallbackFileName),
        };

    private async Task<byte[]?> TryReadLarkFileArtifactBytesAsync(
        FileArtifactRef fileRef,
        int maxBytes,
        string attachmentLabel,
        CancellationToken ct)
    {
        if (_fileArtifactReadPort is null)
        {
            _logger.LogWarning(
                "File artifact read port is unavailable for Lark {AttachmentLabel} attachment chat LLM input: artifactId={ArtifactId} fileId={FileId}",
                attachmentLabel,
                fileRef.ArtifactId,
                fileRef.FileId);
            return null;
        }

        try
        {
            var artifact = await _fileArtifactReadPort.OpenReadAsync(fileRef, ct).ConfigureAwait(false);
            await using var content = artifact.Content;
            return await ReadBoundedAsync(
                    content,
                    maxBytes,
                    NormalizeOptional(artifact.FileRef.FileName) ?? NormalizeOptional(fileRef.FileName),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read Lark {AttachmentLabel} attachment artifact for chat LLM input: artifactId={ArtifactId} fileId={FileId}",
                attachmentLabel,
                fileRef.ArtifactId,
                fileRef.FileId);
            return null;
        }
    }

    internal static async Task<IReadOnlyList<ContentPart>> MaterializeFileRefPartsAsync(
        IReadOnlyList<ContentPart> parts,
        IFileArtifactReadPort? fileArtifactReadPort,
        CancellationToken ct)
    {
        if (parts.Count == 0 || parts.All(static part => part.FileRef is null))
            return parts;

        if (fileArtifactReadPort is null)
            throw new InvalidOperationException("File artifact read port is required to materialize referenced chat media.");

        var materialized = new List<ContentPart>(parts.Count);
        foreach (var part in parts)
        {
            if (part.FileRef is null)
            {
                materialized.Add(part);
                continue;
            }
            if (part.FileRef.ExpiresAtUnixMs > 0 &&
                part.FileRef.ExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                materialized.Add(BuildUnavailableAttachmentPart(part));
                continue;
            }

            FileArtifactContent artifact;
            try
            {
                artifact = await fileArtifactReadPort.OpenReadAsync(ToFileArtifactRef(part.FileRef), ct)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                materialized.Add(BuildUnavailableAttachmentPart(part));
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                materialized.Add(BuildUnavailableAttachmentPart(part));
                continue;
            }
            await using var content = artifact.Content;
            var descriptor = artifact.FileRef;
            ValidateMaterializedPartDescriptor(part, descriptor);
            var bytes = await ReadBoundedAsync(
                    content,
                    MaxAttachmentMaterializationBytes,
                    NormalizeOptional(descriptor.FileName) ?? part.Name,
                    ct)
                .ConfigureAwait(false);
            materialized.Add(part.Kind switch
            {
                ContentPartKind.Text => MaterializeDocumentTextPart(part, descriptor, bytes),
                ContentPartKind.Image => ContentPart.ImagePart(
                    Convert.ToBase64String(bytes),
                    NormalizeImageMediaType(descriptor.MediaType ?? part.MediaType),
                    NormalizeOptional(descriptor.FileName) ?? part.Name),
                ContentPartKind.Audio => ContentPart.AudioPart(
                    Convert.ToBase64String(bytes),
                    NormalizeOptional(descriptor.MediaType) ?? part.MediaType ?? "audio/wav",
                    NormalizeOptional(descriptor.FileName) ?? part.Name),
                ContentPartKind.Video => ContentPart.VideoPart(
                    Convert.ToBase64String(bytes),
                    NormalizeOptional(descriptor.MediaType) ?? part.MediaType ?? "video/mp4",
                    NormalizeOptional(descriptor.FileName) ?? part.Name),
                _ => part,
            });
        }

        return materialized;
    }

    private static ContentPart BuildUnavailableAttachmentPart(ContentPart part)
    {
        var name = NormalizeOptional(part.FileRef?.FileName) ?? NormalizeOptional(part.Name) ?? "attachment";
        if (name.Length > 128)
            name = name[..128];

        return ContentPart.TextPart($"Attachment unavailable: '{name}' has expired or was removed.");
    }

    private static ContentPart MaterializeDocumentTextPart(
        ContentPart part,
        FileArtifactRef descriptor,
        byte[] bytes)
    {
        var mediaType = NormalizeOptional(descriptor.MediaType) ?? NormalizeOptional(part.MediaType);
        var fileName = NormalizeOptional(descriptor.FileName) ?? NormalizeOptional(part.Name);
        string extractedText;
        bool truncated;
        string header;
        if (ResolvePdfMediaType(mediaType, fileName: fileName) is not null)
        {
            (extractedText, truncated) = ExtractPdfText(bytes, MaxInlineDocumentTextChars);
            header = truncated
                ? $"PDF attachment '{fileName ?? "attachment.pdf"}' extracted text (truncated to first {MaxInlineDocumentTextChars} characters):"
                : $"PDF attachment '{fileName ?? "attachment.pdf"}' extracted text:";
        }
        else if (ResolveTextMediaType(mediaType, fileName: fileName) is not null)
        {
            (extractedText, truncated) = ExtractUtf8Text(bytes, MaxInlineDocumentTextChars);
            header = truncated
                ? $"Text attachment '{fileName ?? "attachment.txt"}' content (truncated to first {MaxInlineDocumentTextChars} characters):"
                : $"Text attachment '{fileName ?? "attachment.txt"}' content:";
        }
        else
        {
            return part;
        }

        return new ContentPart
        {
            Kind = ContentPartKind.Text,
            Text = $"{header}\n{extractedText}",
            FileRef = part.FileRef,
            MediaType = mediaType,
            Name = fileName,
        };
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream content,
        int maxBytes,
        string? fileName,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();

            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidOperationException(
                    $"Referenced chat media exceeds the materialization size limit ({maxBytes} bytes): {NormalizeOptional(fileName) ?? "(unnamed file)"}.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static void ValidateMaterializedPartDescriptor(ContentPart part, FileArtifactRef descriptor)
    {
        var mediaType = descriptor.MediaType ?? part.MediaType;
        if (part.Kind == ContentPartKind.Image && !IsSupportedImageMediaType(mediaType))
        {
            throw new InvalidOperationException(
                $"Referenced chat image media type cannot be materialized: {NormalizeOptional(mediaType) ?? "unknown media type"}.");
        }
    }

    private static LlmChatFileRef ToChatFileRef(FileArtifactRef source) =>
        new()
        {
            FileId = NormalizeOptional(source.FileId),
            ArtifactId = NormalizeOptional(source.ArtifactId),
            SourceKind = ToChatFileSourceKind(source.SourceKind),
            SourceMessageId = NormalizeOptional(source.SourceMessageId),
            SourceResourceKey = NormalizeOptional(source.SourceResourceKey),
            FileName = NormalizeOptional(source.FileName),
            MediaType = NormalizeOptional(source.MediaType),
            SizeBytes = source.SizeBytes,
            Sha256 = NormalizeOptional(source.Sha256),
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = NormalizeOptional(source.OwnerRunId),
            OwnerScopeId = NormalizeOptional(source.OwnerScopeId),
        };

    private static FileArtifactRef ToFileArtifactRef(LlmChatFileRef source) =>
        new()
        {
            FileId = NormalizeOptional(source.FileId),
            ArtifactId = NormalizeOptional(source.ArtifactId),
            SourceKind = ToFileArtifactSourceKind(source.SourceKind),
            SourceMessageId = NormalizeOptional(source.SourceMessageId),
            SourceResourceKey = NormalizeOptional(source.SourceResourceKey),
            FileName = NormalizeOptional(source.FileName),
            MediaType = NormalizeOptional(source.MediaType),
            SizeBytes = source.SizeBytes,
            Sha256 = NormalizeOptional(source.Sha256),
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = NormalizeOptional(source.OwnerRunId),
            OwnerScopeId = NormalizeOptional(source.OwnerScopeId),
        };

    private static LlmChatFileSourceKind ToChatFileSourceKind(FileArtifactSourceKind kind) =>
        kind switch
        {
            FileArtifactSourceKind.ChatInput => LlmChatFileSourceKind.ChatInput,
            FileArtifactSourceKind.FormUpload => LlmChatFileSourceKind.FormUpload,
            FileArtifactSourceKind.ConnectedServiceResource => LlmChatFileSourceKind.ConnectedServiceResource,
            FileArtifactSourceKind.ExternalResource => LlmChatFileSourceKind.ExternalResource,
            FileArtifactSourceKind.Generated => LlmChatFileSourceKind.Generated,
            _ => LlmChatFileSourceKind.Unspecified,
        };

    private static FileArtifactSourceKind ToFileArtifactSourceKind(LlmChatFileSourceKind kind) =>
        kind switch
        {
            LlmChatFileSourceKind.ChatInput => FileArtifactSourceKind.ChatInput,
            LlmChatFileSourceKind.FormUpload => FileArtifactSourceKind.FormUpload,
            LlmChatFileSourceKind.ConnectedServiceResource => FileArtifactSourceKind.ConnectedServiceResource,
            LlmChatFileSourceKind.ExternalResource => FileArtifactSourceKind.ExternalResource,
            LlmChatFileSourceKind.Generated => FileArtifactSourceKind.Generated,
            _ => FileArtifactSourceKind.Unspecified,
        };

    private ILarkNyxClient? ResolveLarkResourceDownloadClient(ChatActivity activity, out string? providerSlug)
    {
        providerSlug = NormalizeOptional(activity.TransportExtras?.NyxProviderSlug);
        if (providerSlug is not null && _larkOutboundClientFactory is not null)
            return _larkOutboundClientFactory.ResolveNyxClient(providerSlug);

        return _larkClient;
    }

    private sealed record AttachmentActivity(ChatActivity Activity, IReadOnlyList<AttachmentRef> Attachments);

    private static int CountAttachments(IEnumerable<AttachmentActivity> activities) =>
        activities.Sum(static activity => activity.Attachments.Count);

    private static IEnumerable<AttachmentActivity> SelectAttachmentActivities(
        ChatActivity activity,
        ChatAttachmentInputContext? attachmentContext)
    {
        foreach (var entry in attachmentContext?.RecentAttachmentActivities ?? [])
        {
            var candidate = entry.Activity;
            if (candidate?.Content?.Attachments is { Count: > 0 } recentAttachments)
                yield return new AttachmentActivity(candidate, recentAttachments.Select(attachment => attachment.Clone()).ToArray());
        }

        if (activity.Content?.Attachments is { Count: > 0 } currentAttachments &&
            !HasSameActivity(attachmentContext?.RecentAttachmentActivities, activity.Id))
        {
            yield return new AttachmentActivity(activity, currentAttachments.Select(attachment => attachment.Clone()).ToArray());
        }
    }

    private static bool HasSameActivity(
        IReadOnlyList<RecentConversationAttachmentActivity>? recentActivities,
        string? activityId)
    {
        var normalizedActivityId = NormalizeOptional(activityId);
        if (normalizedActivityId is null || recentActivities is null)
            return false;

        return recentActivities.Any(entry =>
            string.Equals(entry.ActivityId, normalizedActivityId, StringComparison.Ordinal));
    }

    private static string? BuildAttachmentVisibilityInstruction(
        int attachmentCount,
        string reason)
    {
        if (attachmentCount <= 0)
            return null;

        var plural = attachmentCount == 1 ? "attachment" : "attachments";
        return
            $"Attachment visibility warning: The current or recent conversation window contains {attachmentCount} {plural} that are not visible to you because {reason}. Tell the user this limitation plainly when the attachment matters, and do not describe, infer, or pretend to have seen the unavailable attachment content.";
    }

    private static bool IsLarkActivity(ChatActivity activity)
    {
        var platform = NormalizeOptional(activity.TransportExtras?.NyxPlatform) ??
                       NormalizeOptional(activity.ChannelId?.Value);
        return string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLarkImageInputAttachment(AttachmentRef attachment) =>
        attachment.Kind == AttachmentKind.Image ||
        attachment.Kind == AttachmentKind.File && ResolveImageMediaType(attachment.ContentType, fileName: attachment.Name) is not null;

    private static bool IsLarkPdfInputAttachment(AttachmentRef attachment) =>
        attachment.Kind == AttachmentKind.File && ResolvePdfMediaType(attachment.ContentType, fileName: attachment.Name) is not null;

    private static bool IsLarkTextInputAttachment(AttachmentRef attachment) =>
        attachment.Kind == AttachmentKind.File && ResolveTextMediaType(attachment.ContentType, fileName: attachment.Name) is not null;

    private static LarkMessageResourceKind ToLarkMessageResourceKind(AttachmentRef attachment) =>
        attachment.Kind == AttachmentKind.Image
            ? LarkMessageResourceKind.Image
            : LarkMessageResourceKind.File;

    private static bool IsSupportedImageMediaType(string? mediaType) =>
        ResolveImageMediaType(mediaType) is not null;

    private static string NormalizeImageMediaType(string? mediaType) =>
        ResolveImageMediaType(mediaType) ?? "image/png";

    private static string? ResolveDownloadedPdfMediaType(
        string? mediaType,
        string? fallbackMediaType,
        string? fileName,
        string? fallbackFileName)
    {
        var normalized = NormalizeOptional(mediaType)?.ToLowerInvariant();
        if (normalized is not null && normalized is not "application/octet-stream" and not "binary/octet-stream")
            return ResolvePdfMediaType(normalized);

        return ResolvePdfMediaType(fallbackMediaType, fileName: fileName, fallbackFileName: fallbackFileName);
    }

    private static string? ResolveDownloadedImageMediaType(
        string? mediaType,
        string? fallbackMediaType,
        string? fileName,
        string? fallbackFileName)
    {
        var normalized = NormalizeOptional(mediaType)?.ToLowerInvariant();
        if (normalized is not null && normalized is not "application/octet-stream" and not "binary/octet-stream")
            return ResolveImageMediaType(normalized);

        return ResolveImageMediaType(fallbackMediaType, fileName: fileName, fallbackFileName: fallbackFileName);
    }

    private static string? ResolveDownloadedTextMediaType(
        string? mediaType,
        string? fallbackMediaType,
        string? fileName,
        string? fallbackFileName)
    {
        var normalized = NormalizeOptional(mediaType)?.ToLowerInvariant();
        if (normalized is not null && normalized is not "application/octet-stream" and not "binary/octet-stream")
            return ResolveTextMediaType(normalized);

        return ResolveTextMediaType(fallbackMediaType, fileName: fileName, fallbackFileName: fallbackFileName);
    }

    private static string? ResolvePdfMediaType(
        string? mediaType,
        string? fallbackMediaType = null,
        string? fileName = null,
        string? fallbackFileName = null)
    {
        var normalized = NormalizeOptional(mediaType)?.ToLowerInvariant();
        var resolved = normalized == "application/pdf" ? normalized : null;
        if (resolved is not null)
            return resolved;

        if (fallbackMediaType is not null)
            resolved = ResolvePdfMediaType(fallbackMediaType);
        if (resolved is not null)
            return resolved;

        return HasFileExtension(fileName, ".pdf") || HasFileExtension(fallbackFileName, ".pdf")
            ? "application/pdf"
            : null;
    }

    private static string? ResolveTextMediaType(
        string? mediaType,
        string? fallbackMediaType = null,
        string? fileName = null,
        string? fallbackFileName = null)
    {
        var normalized = NormalizeOptional(mediaType)?.ToLowerInvariant();
        var resolved = normalized switch
        {
            not null when normalized.StartsWith("text/", StringComparison.Ordinal) => normalized,
            "application/json" or "application/yaml" or "application/x-yaml" => normalized,
            _ => null,
        };
        if (resolved is not null)
            return resolved;

        if (fallbackMediaType is not null)
            resolved = ResolveTextMediaType(fallbackMediaType);
        if (resolved is not null)
            return resolved;

        return ResolveTextMediaTypeFromFileName(fileName) ?? ResolveTextMediaTypeFromFileName(fallbackFileName);
    }

    private static string? ResolveImageMediaType(
        string? mediaType,
        string? fallbackMediaType = null,
        string? fileName = null,
        string? fallbackFileName = null)
    {
        var normalized = NormalizeOptional(mediaType)?.ToLowerInvariant();
        var resolved = normalized switch
        {
            "image" => "image/png",
            "image/jpg" => "image/jpeg",
            "image/png" or "image/jpeg" or "image/webp" or "image/gif" => normalized,
            _ => null,
        };
        if (resolved is not null)
            return resolved;

        if (fallbackMediaType is not null)
            resolved = ResolveImageMediaType(fallbackMediaType);
        if (resolved is not null)
            return resolved;

        return ResolveImageMediaTypeFromFileName(fileName) ?? ResolveImageMediaTypeFromFileName(fallbackFileName);
    }

    private static string? ResolveTextMediaTypeFromFileName(string? fileName)
    {
        if (HasFileExtension(fileName, ".txt") ||
            HasFileExtension(fileName, ".text") ||
            HasFileExtension(fileName, ".md") ||
            HasFileExtension(fileName, ".markdown") ||
            HasFileExtension(fileName, ".log"))
            return "text/plain";
        if (HasFileExtension(fileName, ".json") ||
            HasFileExtension(fileName, ".jsonl"))
            return "application/json";
        if (HasFileExtension(fileName, ".yaml") ||
            HasFileExtension(fileName, ".yml"))
            return "application/yaml";
        if (HasFileExtension(fileName, ".csv"))
            return "text/csv";

        return null;
    }

    private static string? ResolveImageMediaTypeFromFileName(string? fileName)
    {
        if (HasFileExtension(fileName, ".jpg") ||
            HasFileExtension(fileName, ".jpeg"))
            return "image/jpeg";
        if (HasFileExtension(fileName, ".png"))
            return "image/png";
        if (HasFileExtension(fileName, ".webp"))
            return "image/webp";
        if (HasFileExtension(fileName, ".gif"))
            return "image/gif";

        return null;
    }

    private static bool HasFileExtension(string? fileName, string extension)
    {
        var normalized = NormalizeOptional(fileName)?.ToLowerInvariant();
        return normalized?.EndsWith(extension, StringComparison.Ordinal) == true;
    }

    private static (string Text, bool Truncated) ExtractUtf8Text(byte[] content, int maxChars)
    {
        var text = Encoding.UTF8.GetString(content);
        var truncated = text.Length > maxChars;
        return (truncated ? text[..maxChars].Trim() : text.Trim(), truncated);
    }

    private static (string Text, bool Truncated) ExtractPdfText(byte[] content, int maxChars)
    {
        using var document = PdfDocument.Open(content);
        var builder = new StringBuilder(Math.Min(maxChars, 4096));
        var truncated = false;
        foreach (var page in document.GetPages())
        {
            truncated |= WouldExceedLimit(builder, page.Text, maxChars);
            AppendCapped(builder, page.Text, maxChars);
            if (builder.Length >= maxChars)
            {
                truncated = true;
                break;
            }

            AppendCapped(builder, "\n", maxChars);
        }

        return (builder.ToString().Trim(), truncated);
    }

    private static void AppendCapped(StringBuilder builder, string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || builder.Length >= maxChars)
            return;

        var remaining = maxChars - builder.Length;
        builder.Append(value.Length <= remaining ? value : value[..remaining]);
    }

    private static bool WouldExceedLimit(StringBuilder builder, string? value, int maxChars) =>
        !string.IsNullOrEmpty(value) && value.Length > maxChars - builder.Length;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Reasoning content is ephemeral provider output, never conversation input: replaying a
    // prior turn's reasoning_content to the provider violates the reasoning-model contract
    // (DeepSeek documents it as a request error; through the NyxID proxy it instead silently
    // derails generation — the 2026-06-12 prod incident where every turn in a
    // reasoning-history-bearing conversation completed empty). History entries keep the
    // reasoning durably for audit; the rehydration boundary strips it from LLM input.
    private static ChatMessage ToChatMessage(ConversationHistoryEntry entry) =>
        new()
        {
            Role = string.IsNullOrWhiteSpace(entry.Role) ? "user" : entry.Role,
            Content = string.IsNullOrEmpty(entry.Content) ? null : entry.Content,
            ReasoningContent = null,
            ContentParts = entry.ContentParts.Select(ContentPartProtoMapper.FromProto).ToArray(),
            ToolCallId = string.IsNullOrEmpty(entry.ToolCallId) ? null : entry.ToolCallId,
            ToolCalls = entry.ToolCalls.Select(ToToolCall).ToArray(),
        };

    // Assistant entries with no wire-visible content (no text, no content parts, no tool
    // calls — e.g. reasoning-only turns persisted before AgentRunGAgent stopped appending
    // them to durable history) are skipped on replay: providers drop bare reasoning on
    // assistant history messages, so such entries degenerate into empty assistant turns
    // that corrupt every later request in the conversation.
    private static bool IsReplayableHistoryEntry(ConversationHistoryEntry entry)
    {
        if (!string.Equals(entry.Role, "assistant", StringComparison.Ordinal))
            return true;

        return !string.IsNullOrWhiteSpace(entry.Content)
               || entry.ContentParts.Count > 0
               || entry.ToolCalls.Count > 0;
    }

    private static ConversationHistoryEntry ToConversationHistoryEntry(ChatMessage message)
    {
        var entry = new ConversationHistoryEntry
        {
            Role = message.Role ?? string.Empty,
            Content = message.Content ?? string.Empty,
            ReasoningContent = message.ReasoningContent ?? string.Empty,
            ToolCallId = message.ToolCallId ?? string.Empty,
        };
        entry.ContentParts.AddRange((message.ContentParts ?? []).Select(ToPersistedContentPart));
        entry.ToolCalls.AddRange((message.ToolCalls ?? []).Select(ToConversationToolCallEntry));
        return entry;
    }

    private static Aevatar.AI.Abstractions.ChatContentPart ToPersistedContentPart(ContentPart part)
    {
        var persisted = ContentPartProtoMapper.ToProto(part);
        if (persisted.Kind == Aevatar.AI.Abstractions.ChatContentPartKind.Text &&
            HasFileRefIdentity(persisted.FileRef))
            persisted.Text = string.Empty;
        return persisted;
    }

    private static bool HasFileRefIdentity(Aevatar.AI.Abstractions.ChatFileRef? fileRef) =>
        fileRef is not null &&
        (!string.IsNullOrWhiteSpace(fileRef.FileId) || !string.IsNullOrWhiteSpace(fileRef.ArtifactId));

    private static ToolCall ToToolCall(ConversationToolCallEntry entry) =>
        new()
        {
            Id = entry.Id ?? string.Empty,
            Name = entry.Name ?? string.Empty,
            ArgumentsJson = entry.ArgumentsJson ?? string.Empty,
        };

    private static ConversationToolCallEntry ToConversationToolCallEntry(ToolCall call) =>
        new()
        {
            Id = call.Id ?? string.Empty,
            Name = call.Name ?? string.Empty,
            ArgumentsJson = call.ArgumentsJson ?? string.Empty,
        };

    private static bool ShouldStreamVisibleReply(string accumulatedText)
    {
        if (string.IsNullOrWhiteSpace(accumulatedText))
            return false;

        return TextToolCallParser.Parse(accumulatedText).ToolCalls.Count == 0;
    }

    private static string? BuildSkillRecoveryStreamingStatus(AgentToolExecutionContext? toolContext)
    {
        var recovery = toolContext?.SkillRecovery;
        if (recovery is not { RequireInitialOrnnSearch: true })
        {
            return null;
        }

        if (recovery.DiscoveryRequested)
            return "正在查找可用技能...";

        var commandLabel = recovery.OriginalCommand?.Trim();
        if (string.IsNullOrWhiteSpace(commandLabel))
            commandLabel = recovery.CommandName?.Trim();

        return string.IsNullOrWhiteSpace(commandLabel)
            ? null
            : $"正在处理 `{commandLabel}`, 加载技能并扫描数据中...";
    }

    // ADR-0021 §6 / canon §8 cross-round usage aggregation — each provider round
    // reports its own Usage; the actor-edge closeout carries the sum.
    private static ReplyTokenUsage? SumUsage(ReplyTokenUsage? acc, ReplyTokenUsage? add)
    {
        if (add is null) return acc;
        if (acc is null) return add;
        return new ReplyTokenUsage(
            acc.PromptTokens + add.PromptTokens,
            acc.CompletionTokens + add.CompletionTokens,
            acc.TotalTokens + add.TotalTokens);
    }

    private static ReplyTokenUsage MapUsage(TokenUsage usage) =>
        new(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);

    private ChatRuntime BuildRuntime(
        ChatActivity activity,
        LLMControlContext llmControl,
        AgentToolExecutionContext toolContext,
        IReadOnlyDictionary<string, string> externalMetadata,
        ToolManager tools,
        string? attachmentVisibilityInstruction,
        ConversationContextPromptLayer? conversationLayer = null)
    {
        var history = new global::Aevatar.AI.Core.Chat.ChatHistory
        {
            MaxMessages = MaxWorkingSetMessages,
        };
        return new ChatRuntime(
            providerFactory: ResolveProvider,
            history: history,
            toolLoop: new ToolCallLoop(
                tools,
                hooks: null,
                llmMiddlewares: _llmMiddlewares,
                toolExecutionPort: _toolExecutionPort,
                approvalContinuationMode: AgentToolApprovalContinuationMode.ActorOwned),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages =
                [
                    ChatMessage.System(BuildSystemPrompt(
                        externalMetadata,
                        toolContext,
                        attachmentVisibilityInstruction,
                        conversation: conversationLayer)),
                ],
                Metadata = externalMetadata,
                ToolContext = toolContext,
                LlmControl = llmControl,
                RoutingContext = llmControl.ToRoutingContext(),
                Tools = FilterValidTools(tools),
            },
            agentMiddlewares: _agentMiddlewares,
            llmMiddlewares: _llmMiddlewares,
            agentId: activity.Conversation?.CanonicalKey,
            agentName: "NyxIdConversationReply",
            suppressToolCallRoundText: true);
    }

    private static int ResolveMaxToolRounds(LLMControlContext llmControl) =>
        llmControl.MaxToolRoundsOverride is > 0
            ? llmControl.MaxToolRoundsOverride.Value
            : MaxToolRounds;

    private async Task<EffectiveReplyPlan> BuildEffectiveReplyPlanAsync(
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct)
    {
        var effective = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        var effectiveControl = llmControl ?? LLMControlContext.Empty;
        effectiveControl = effectiveControl with { SenderNyxIdAccessToken = null };
        var effectiveToolContext = toolContext;
        Dictionary<string, string>? ownerFallback = null;
        LLMControlContext? ownerFallbackControl = null;
        AgentToolExecutionContext? ownerFallbackToolContext = null;

        // Issue #513 phase 3: prefs override chain is sender → bot-owner →
        // provider default. The bot owner's prefs are already pinned upstream
        // by OwnerLlmConfigApplier (channel inbound) or by direct
        // INyxIdUserLlmPreferencesStore reads (Studio API / streaming proxy),
        // so this generator only has to layer sender overrides on top when
        // the inbound carries a binding-id. SetIfFilled is field-level, so a
        // sender who set DefaultModel but not PreferredRoute still inherits
        // the bot owner's route from the upstream-pinned metadata. If a
        // sender-owned attempt fails, we retry once with this owner snapshot.
        var senderBindingId = toolContext?.SenderBinding.BindingId?.Trim();
        var disableTools = IsChannelTurn(effective) && string.IsNullOrWhiteSpace(senderBindingId);
        if (!string.IsNullOrWhiteSpace(senderBindingId))
        {
            var ownerSnapshot = CreateOwnerFallbackSnapshot(effective);
            ownerFallbackControl = effectiveControl with { SenderNyxIdAccessToken = null };
            ownerFallbackToolContext = ClearSenderBinding(effectiveToolContext);
            ownerFallback = ownerSnapshot;
            var senderToken = llmControl?.SenderNyxIdAccessToken?.Trim();

            if (_preferencesStore is not null)
            {
                var preferenceResult = await ApplyPreferencesAsync(senderBindingId, effectiveControl, ct);
                effectiveControl = preferenceResult.Control;
                var applied = preferenceResult.Application;
                _logger.LogInformation(
                    "Resolved sender LLM config: bindingId={BindingId} applied={Applied} modelApplied={ModelApplied} routeApplied={RouteApplied} maxToolRoundsApplied={MaxToolRoundsApplied} effectiveModel={Model} effectiveRoute={Route} effectiveMaxToolRounds={MaxToolRounds}",
                    senderBindingId,
                    applied.AnyApplied,
                    applied.ModelApplied,
                    applied.RouteApplied,
                    applied.MaxToolRoundsApplied,
                    string.IsNullOrWhiteSpace(effectiveControl.ModelOverride) ? "<server-default>" : effectiveControl.ModelOverride,
                    string.IsNullOrWhiteSpace(effectiveControl.NyxIdRoutePreference) ? "<server-default>" : effectiveControl.NyxIdRoutePreference,
                    effectiveControl.MaxToolRoundsOverride);
                if (applied.RouteApplied && string.IsNullOrWhiteSpace(senderToken))
                {
                    effectiveControl = ownerFallbackControl ?? LLMControlContext.Empty;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Sender binding is present but LLM preferences store is unavailable; using bot owner/default LLM config: bindingId={BindingId}",
                    senderBindingId);
            }

            if (!string.IsNullOrWhiteSpace(senderToken))
            {
                effectiveControl = effectiveControl with
                {
                    NyxIdAccessToken = senderToken,
                    NyxIdOrgToken = senderToken,
                    SenderNyxIdAccessToken = senderToken,
                };
            }
        }

        if (_userMemoryPromptContextProvider is not null)
        {
            var promptSection = await _userMemoryPromptContextProvider.BuildAsync(2000, ct);
            if (!string.IsNullOrWhiteSpace(promptSection))
            {
                effectiveControl = effectiveControl with { UserMemoryPrompt = promptSection };
                if (ownerFallback is not null)
                    ownerFallbackControl = (ownerFallbackControl ?? effectiveControl) with
                    {
                        UserMemoryPrompt = promptSection,
                    };
            }
        }

        return new EffectiveReplyPlan(
            effective,
            effectiveControl,
            effectiveToolContext,
            ownerFallback,
            ownerFallbackControl,
            ownerFallbackToolContext,
            disableTools);
    }

    /// <summary>
    /// Read prefs for the bound sender and overwrite the matching metadata
    /// keys. Field-level: empty fields on the sender's record are skipped so
    /// the bot owner's value stays intact. User-config failures degrade to
    /// "no sender override" rather than failing the LLM turn.
    /// </summary>
    private async Task<SenderPreferenceResult> ApplyPreferencesAsync(
        string senderBindingId,
        LLMControlContext effectiveControl,
        CancellationToken ct)
    {
        if (_preferencesStore is null)
            return new SenderPreferenceResult(effectiveControl, new SenderPreferenceApplication(false, false, false));

        NyxIdUserLlmPreferences preferences;
        try
        {
            preferences = await _preferencesStore.GetForBindingAsync(senderBindingId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load sender LLM config; using bot owner/default LLM config: bindingId={BindingId}",
                senderBindingId);
            return new SenderPreferenceResult(effectiveControl, new SenderPreferenceApplication(false, false, false));
        }

        var modelApplied = preferences.Status == LLMSelectionPersistenceStatus.Ready &&
                           preferences.Selection.ModelSelection?.Kind == LLMModelSelectionKind.ExplicitModel;
        var routeApplied = preferences.Status == LLMSelectionPersistenceStatus.Ready;
        var roundsApplied = preferences.MaxToolRounds > 0;
        effectiveControl = preferences.ApplyTo(effectiveControl);
        return new SenderPreferenceResult(
            effectiveControl,
            new SenderPreferenceApplication(modelApplied, routeApplied, roundsApplied));
    }

    private static Dictionary<string, string> CreateOwnerFallbackSnapshot(Dictionary<string, string> effective)
    {
        var snapshot = new Dictionary<string, string>(effective, StringComparer.Ordinal);
        snapshot.Remove(LLMRequestMetadataKeys.SenderBindingId);
        return snapshot;
    }

    private static AgentToolExecutionContext? ClearSenderBinding(AgentToolExecutionContext? context) =>
        context == null
            ? null
            : context with { SenderBinding = AgentToolSenderBindingContext.Empty };

    private static bool IsChannelTurn(IReadOnlyDictionary<string, string> metadata) =>
        metadata.ContainsKey(ChannelMetadataKeys.Platform) &&
        metadata.ContainsKey(ChannelMetadataKeys.SenderId) &&
        metadata.ContainsKey(ChannelMetadataKeys.MessageId);

    // Channel-relay detection for the human-only tool gate (issue #2580 Item 2). It must read the
    // TYPED channel context, not metadata: channel.platform / sender_id / message_id are owned control
    // keys that AgentToolExecutionContextMapper.StripOwnedControlKeys removes before the step state is
    // persisted, so from the second LLM round the per-step metadata no longer carries them. The typed
    // toolContext.Channel is an identity fact that survives stripping and every round, so the gate
    // stays on for the whole relay turn (mirrors IsChannelTurn's Platform+SenderId+MessageId shape).
    private static bool IsChannelRelayTurn(AgentToolExecutionContext? toolContext)
    {
        var channel = toolContext?.Channel;
        return channel is not null &&
            !string.IsNullOrWhiteSpace(channel.Platform) &&
            !string.IsNullOrWhiteSpace(channel.SenderId) &&
            !string.IsNullOrWhiteSpace(channel.MessageId);
    }

    private async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
        bool isChannelTurn,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct)
    {
        var isNyxIdChatTurn = IsNyxIdChatTurn(toolContext);
        var toolSources = isNyxIdChatTurn ? _nyxIdChatToolSources : _toolSources;
        if (toolSources.Count == 0)
            return [];

        var discovery = await _toolDiscoveryService
            .DiscoverAsync(toolSources, toolContext ?? AgentToolExecutionContext.Empty, ct)
            .ConfigureAwait(false);
        if (!discovery.IsSuccess)
        {
            _logger.LogError(
                "Channel tool catalog discovery failed closed. code={FailureCode} tool={ToolName} source={SourceType} conflictingSource={ConflictingSourceType}",
                discovery.Failure!.Code,
                discovery.Failure.ToolName,
                discovery.Failure.SourceType,
                discovery.Failure.ConflictingSourceType);
            throw new AgentToolDiscoveryException(discovery.Failure);
        }

        var discovered = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceGroup in discovery.Entries.GroupBy(static entry => entry.SourceType, StringComparer.Ordinal))
        {
            var tools = sourceGroup.Select(static entry => entry.Tool).ToArray();
            var excludedDirectChannelToolNames = new List<string>();
            var excludedHumanSessionToolNames = new List<string>();
            foreach (var tool in tools)
            {
                // Channel-side exclusion by GENERIC capability, not by tool name: a tool that
                // declares AgentToolCapabilities.ExcludeFromDirectChannelChat completes its work
                // off-chat (e.g. delivered to /admin#/observatory), so surfacing it on this
                // direct-channel/Lark agent would let the model silently route a chat user's
                // request away from their chat. Such tools stay in the global catalog for the
                // workflow allowlist path; the exclusion is channel-side only. No channel agent
                // depended on these tools, so this changes no existing channel flow.
                if (IsExcludedFromDirectChannelChat(tool))
                {
                    excludedDirectChannelToolNames.Add(tool.Name);
                    continue;
                }

                if (isNyxIdChatTurn &&
                    DeclaresCapability(tool, AgentToolCapabilities.ExcludeFromNyxIdChat))
                {
                    continue;
                }

                var requiresHumanSession =
                    DeclaresCapability(tool, AgentToolCapabilities.RequiresHumanSession);
                var hasSourceReadableBearer = !string.IsNullOrWhiteSpace(
                    AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(toolContext?.Credentials));
                if (requiresHumanSession &&
                    (isChannelTurn || (isNyxIdChatTurn && !hasSourceReadableBearer)))
                {
                    excludedHumanSessionToolNames.Add(tool.Name);
                    continue;
                }

                discovered.Add(tool.Name, tool);
            }

            if (isChannelTurn)
            {
                _logger.LogInformation(
                    "Channel tool source discovery: source={SourceType}, discoveredTools={DiscoveredTools}, excludedDirectChannelTools={ExcludedDirectChannelTools}, excludedHumanSessionTools={ExcludedHumanSessionTools}",
                    sourceGroup.Key,
                    FormatToolNames(tools.Select(static tool => tool.Name)),
                    FormatToolNames(excludedDirectChannelToolNames),
                    FormatToolNames(excludedHumanSessionToolNames));
            }
        }

        var effectiveTools = discovered.Values.ToArray();
        if (isChannelTurn)
        {
            _logger.LogInformation(
                "Channel effective tool discovery completed: sourceCount={SourceCount}, toolCount={ToolCount}, tools={Tools}",
                toolSources.Count,
                effectiveTools.Length,
                FormatToolNames(effectiveTools.Select(static tool => tool.Name)));
        }

        return effectiveTools;
    }

    // A tool is hidden from the direct channel/chat surface when it self-declares the
    // generic ExcludeFromDirectChannelChat capability via IAgentToolCapabilityDescriptor.
    // The channel path never inspects a specific tool name; eligibility is a property of
    // the tool, keeping channel routing agnostic to individual tool/skill identities.
    private static bool IsExcludedFromDirectChannelChat(IAgentTool tool) =>
        DeclaresCapability(tool, AgentToolCapabilities.ExcludeFromDirectChannelChat);

    private static bool IsNyxIdChatTurn(AgentToolExecutionContext? toolContext) =>
        string.Equals(
            toolContext?.Channel.Platform,
            NyxIdChatServiceDefaults.ServiceId,
            StringComparison.OrdinalIgnoreCase);

    private static bool DeclaresCapability(IAgentTool tool, string capability) =>
        tool is IAgentToolCapabilityDescriptor descriptor &&
        descriptor.Capabilities.Any(declared =>
            string.Equals(declared, capability, StringComparison.OrdinalIgnoreCase));

    private static string FormatToolNames(IEnumerable<string?> toolNames)
    {
        var names = toolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!.Trim())
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0 ? "<none>" : string.Join(",", names);
    }

    private ILLMProvider ResolveProvider()
    {
        var available = _llmProviderFactory.GetAvailableProviders();
        if (available.Any(name => string.Equals(name, NyxIdChatServiceDefaults.ProviderName, StringComparison.OrdinalIgnoreCase)))
            return _llmProviderFactory.GetProvider(NyxIdChatServiceDefaults.ProviderName);

        return _llmProviderFactory.GetDefault();
    }

    private static IReadOnlyList<IAgentTool>? FilterValidTools(ToolManager tools)
    {
        if (!tools.HasTools)
            return null;

        var valid = tools.GetAll()
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .ToArray();
        return valid.Length == 0 ? null : valid;
    }

    private async Task<ConversationContextPromptLayer?> MaterializeConversationContextLayerAsync(
        ChatAttachmentInputContext? attachmentContext,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        // Fix (review round 1, F3):
        //   Conversation CanonicalKey was incorrectly used as a ContentArtifact owner scope.
        //   Only the typed caller scope is used; a missing scope degrades without identity guessing.
        return await ContentArtifactConversationPromptLayerMaterializer.MaterializeOrDegradeAsync(
                _contentArtifactPromptLayerMaterializer,
                attachmentContext?.ContextAttachments,
                NormalizeOptional(toolContext.Caller.ScopeId),
                NormalizeOptional(toolContext.Caller.OwnerSubject),
                ct,
                _logger)
            .ConfigureAwait(false);
    }

    private string BuildSystemPrompt(
        IReadOnlyDictionary<string, string> metadata,
        AgentToolExecutionContext toolContext,
        string? attachmentVisibilityInstruction = null,
        string? runtimeNotice = null,
        AgentTurnToolCatalog? turnCatalog = null,
        ConversationContextPromptLayer? conversation = null)
    {
        var runtimeFacts = new StringBuilder();
        AppendRuntimeFact(
            runtimeFacts,
            NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(_relayOptions));
        var channelContext = ChannelContextMiddleware.BuildChannelContextSection(
            metadata,
            toolContext.Channel.IdentityHints);
        AppendRuntimeFact(runtimeFacts, channelContext);

        if (_localSkillCatalog is not null && _localSkillCatalog.Count > 0)
        {
            var skillSection = _localSkillCatalog.BuildSystemPromptSection();
            AppendRuntimeFact(runtimeFacts, skillSection);
        }

        AppendRuntimeFact(runtimeFacts, attachmentVisibilityInstruction);
        AppendRuntimeFact(runtimeFacts, BuildCurrentInputFileRefsSection(toolContext.InputFileRefs));
        AppendRuntimeFact(runtimeFacts, runtimeNotice);

        var global = _overlayProvider?.GetCurrent(new SystemSkillOverlayRequest(
            ResolveChannelPlatform(toolContext, metadata),
            toolContext.Credentials.NyxIdAccessToken));
        var runtime = runtimeFacts.Length == 0
            ? null
            : new RuntimeFactsPromptLayer(
                runtimeFacts.ToString(),
                new RuntimeFactsPromptProvenance("nyxid-relay-runtime"));
        return SystemPromptLayerComposer.Compose(
            NyxIdChatSystemPrompt.Value,
            _builtInPromptFloorProvider.GetFloor(),
            global,
            turnCatalog?.ProfilePromptLayer,
            turnCatalog?.SelectedSkillPromptLayer,
            runtime,
            conversation).Prompt;
    }

    // The typed channel context is the authoritative platform source: the per-step plan path hands
    // BuildSystemPrompt the STRIPPED external metadata (StripOwnedControlKeys removes channel.platform),
    // so reading metadata alone would silently degrade platform-scoped overlay members to global-only
    // on every AgentRun turn. Metadata stays as the fallback for callers without a typed context.
    private static string? ResolveChannelPlatform(
        AgentToolExecutionContext toolContext,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (!string.IsNullOrWhiteSpace(toolContext.Channel.Platform))
            return toolContext.Channel.Platform;

        return metadata.TryGetValue(ChannelMetadataKeys.Platform, out var platform) && !string.IsNullOrWhiteSpace(platform)
            ? platform
            : null;
    }

    private static readonly JsonFormatter InputFileRefJsonFormatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(false)
            .WithPreserveProtoFieldNames(true)
            .WithFormatEnumsAsIntegers(true));

    private static string? BuildCurrentInputFileRefsSection(IReadOnlyList<Aevatar.AI.Abstractions.ChatFileRef> fileRefs)
    {
        var handles = fileRefs
            .Where(HasFileRefIdentity)
            .Select(ToPromptFileHandle)
            .ToArray();
        if (handles.Length == 0)
            return null;

        var builder = new StringBuilder();
        builder.AppendLine("## Current input files");
        builder.AppendLine("The current turn includes runtime-owned typed file references. When a tool accepts file input, pass one exact handle under `input_parts[].file_ref`; do not invent attachment identifiers or report that no file reference exists.");
        foreach (var handle in handles)
            builder.AppendLine($"- file_ref: {InputFileRefJsonFormatter.Format(handle)}");

        return builder.ToString();
    }

    private static Aevatar.AI.Abstractions.ChatFileRef ToPromptFileHandle(Aevatar.AI.Abstractions.ChatFileRef fileRef) =>
        new()
        {
            FileId = fileRef.FileId,
            ArtifactId = fileRef.ArtifactId,
            SourceKind = fileRef.SourceKind,
        };

    private static void AppendRuntimeFact(StringBuilder builder, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;
        if (builder.Length > 0)
            builder.Append("\n\n");
        builder.Append(content.Trim());
    }
}
