using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LlmChatFileRef = Aevatar.AI.Abstractions.LLMProviders.ChatFileRef;
using LlmChatFileSourceKind = Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind;
using WorkflowFileRef = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowFileRef;

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
    internal const string AttachmentPolicyErrorCode = "attachment_policy_rejected";

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

    private readonly ILLMProviderFactory _llmProviderFactory;
    private readonly IReadOnlyList<IAgentToolSource> _toolSources;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly IToolApprovalHandler? _approvalHandler;
    private readonly LocalSkillCatalog? _localSkillCatalog;
    private readonly IRemoteSkillFetcher? _remoteSkillFetcher;
    private readonly global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? _relayOptions;
    private readonly INyxIdUserLlmPreferencesStore? _preferencesStore;
    private readonly IUserMemoryStore? _userMemoryStore;
    private readonly ILarkNyxClient? _larkClient;
    private readonly IWorkflowFileIngressPort? _fileIngressPort;
    private readonly IWorkflowFileArtifactReadPort? _fileArtifactReadPort;
    private readonly ILarkOutboundClientFactory? _larkOutboundClientFactory;
    private readonly ISystemSkillOverlayProvider? _overlayProvider;
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
        IEnumerable<IAgentToolSource>? toolSources = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        LocalSkillCatalog? localSkillCatalog = null,
        IRemoteSkillFetcher? remoteSkillFetcher = null,
        global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions = null,
        INyxIdUserLlmPreferencesStore? preferencesStore = null,
        IUserMemoryStore? userMemoryStore = null,
        ILarkNyxClient? larkClient = null,
        IWorkflowFileIngressPort? fileIngressPort = null,
        IWorkflowFileArtifactReadPort? fileArtifactReadPort = null,
        IToolApprovalHandler? approvalHandler = null,
        ILogger<NyxIdConversationReplyGenerator>? logger = null,
        ISystemSkillOverlayProvider? overlayProvider = null,
        ILarkOutboundClientFactory? larkOutboundClientFactory = null)
    {
        _llmProviderFactory = llmProviderFactory ?? throw new ArgumentNullException(nameof(llmProviderFactory));
        _toolSources = (toolSources ?? []).ToArray();
        _agentMiddlewares = (agentMiddlewares ?? []).ToArray();
        _toolMiddlewares = (toolMiddlewares ?? []).ToArray();
        _llmMiddlewares = (llmMiddlewares ?? []).ToArray();
        _approvalHandler = approvalHandler;
        _localSkillCatalog = localSkillCatalog;
        _remoteSkillFetcher = remoteSkillFetcher;
        _relayOptions = relayOptions;
        _preferencesStore = preferencesStore;
        _userMemoryStore = userMemoryStore;
        _larkClient = larkClient;
        _fileIngressPort = fileIngressPort;
        _fileArtifactReadPort = fileArtifactReadPort;
        _larkOutboundClientFactory = larkOutboundClientFactory;
        _overlayProvider = overlayProvider;
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
        var primaryTools = await BuildTurnToolsAsync(replyPlan.DisableTools, isChannelTurn, ct);

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
        catch (AttachmentPolicyException ex)
        {
            _logger.LogWarning(
                ex,
                "Chat attachment rejected before LLM generation: activity={ActivityId} reason={Reason}",
                activity.Id,
                ex.Reason);
            return await BuildControlledAttachmentFailureAsync(ex, streamingSink, ct).ConfigureAwait(false);
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

            var fallbackTools = await BuildTurnToolsAsync(disableTools: true, isChannelTurn, ct);
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
        CancellationToken ct) =>
        await BuildStepPlanCoreAsync(
                activity,
                metadata,
                llmControl,
                toolContext,
                priorHistory,
                attachmentContext,
                forceDisableTools,
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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(metadata);

        var replyPlan = await BuildEffectiveReplyPlanAsync(metadata, llmControl, toolContext, ct);
        var provider = ResolveProvider();
        var disableTools = forceDisableTools || replyPlan.DisableTools;
        var tools = await BuildTurnToolsAsync(disableTools, IsChannelRelayTurn(toolContext), ct);
        var externalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(replyPlan.Primary);
        var effectiveToolContext = replyPlan.PrimaryControl.ToToolContext(
            replyPlan.PrimaryToolContext ?? AgentToolExecutionContext.Empty with
            {
                ExternalMetadata = externalMetadata,
            });
        var input = await BuildUserInputPartsAsync(
                activity,
                provider,
                attachmentContext,
                ct)
            .ConfigureAwait(false);
        if (input.AttachmentPolicyFailure is { } policyFailure)
            return BuildTerminalAttachmentFailureStepPlan(
                policyFailure,
                externalMetadata,
                replyPlan.PrimaryControl,
                effectiveToolContext,
                ResolveMaxToolRounds(replyPlan.PrimaryControl),
                disableTools,
                replyPlan.OwnerFallbackControl,
                replyPlan.OwnerFallbackToolContext);

        var runtime = BuildRuntime(
            activity,
            replyPlan.PrimaryControl,
            effectiveToolContext,
            externalMetadata,
            tools,
            input.AttachmentVisibilityInstruction);

        // The unbound-sender gate (issue #1318) detaches the entire tool surface while
        // the kernel prompt still documents those tools; without this override the
        // model reports the capability as missing instead of the actual, recoverable
        // reason. Keyed on the plan's own gate (not forceDisableTools): per-step rebuilds
        // discard InitialMessages, so this notice is stamped exactly once per run.
        var initialMessages = new List<ChatMessage>
        {
            ChatMessage.System(AppendSystemPromptSuffix(
                BuildSystemPrompt(externalMetadata, effectiveToolContext, input.AttachmentVisibilityInstruction),
                replyPlan.DisableTools ? UnboundSenderToolsDisabledNotice : null)),
        };
        initialMessages.AddRange((priorHistory ?? []).Where(IsReplayableHistoryEntry).TakeLast(MaxRecentPriorHistoryMessages).Select(ToChatMessage));
        initialMessages.Add(ChatMessage.User(input.Parts, input.Text));

        return new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(),
            externalMetadata,
            replyPlan.PrimaryControl,
            effectiveToolContext,
            initialMessages,
            ResolveMaxToolRounds(replyPlan.PrimaryControl),
            disableTools,
            replyPlan.OwnerFallbackControl,
            replyPlan.OwnerFallbackToolContext);
    }

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    private async Task<ToolManager> BuildTurnToolsAsync(bool disableTools, bool isChannelTurn, CancellationToken ct)
    {
        var tools = new ToolManager();
        if (disableTools)
            return tools;

        foreach (var tool in await DiscoverToolsAsync(isChannelTurn, ct))
            tools.Register(tool);

        // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
        //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
        //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
        if ((_localSkillCatalog is not null || _remoteSkillFetcher is not null) &&
            tools.Get("use_skill") is null)
        {
            tools.Register(new UseSkillTool(_localSkillCatalog ?? new LocalSkillCatalog(), _remoteSkillFetcher));
        }

        return tools;
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
        if (input.AttachmentPolicyFailure is { } policyFailure)
            return await BuildControlledAttachmentFailureAsync(policyFailure, streamingSink, ct).ConfigureAwait(false);
        input = await MaterializeUserInputPartsAsync(input, ct).ConfigureAwait(false);

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
                toolMiddlewares: BuildToolMiddlewaresForTurn(),
                llmMiddlewares: _llmMiddlewares),
            hooks: null,
            requestBuilder: () => new LLMRequest
            {
                Messages =
                [
                    ChatMessage.System(AppendSystemPromptSuffix(
                        BuildSystemPrompt(effectiveMetadata, toolContext, input.AttachmentVisibilityInstruction),
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
        string? AttachmentVisibilityInstruction = null,
        AttachmentPolicyException? AttachmentPolicyFailure = null);

    private async Task<UserInputParts> BuildUserInputPartsAsync(
        ChatActivity activity,
        ILLMProvider provider,
        ChatAttachmentInputContext? attachmentContext,
        CancellationToken ct)
    {
        var text = activity.Content?.Text ?? string.Empty;
        var parts = new List<ContentPart> { ContentPart.TextPart(text) };
        var attachments = SelectAttachmentActivities(activity, attachmentContext).ToArray();
        if (attachments.Length == 0)
            return new UserInputParts(text, parts);

        if (!provider.Capabilities.SupportsInput(ContentPartKind.Image))
        {
            return new UserInputParts(
                text,
                parts,
                BuildAttachmentVisibilityInstruction(
                    CountAttachments(attachments),
                    "the selected LLM route does not support image input"));
        }

        if (_larkClient is null && _larkOutboundClientFactory is null)
        {
            return new UserInputParts(
                text,
                parts,
                BuildAttachmentVisibilityInstruction(
                    CountAttachments(attachments),
                    "Lark resource download is not available in this runtime"));
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
                    "the Lark user credential needed to download the attachment is unavailable"));
        }

        var unseenCount = 0;
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
                if (attachment.Kind != AttachmentKind.Image)
                {
                    unseenCount++;
                    continue;
                }

                if (attachment.SizeBytes > MaxAttachmentMaterializationBytes)
                {
                    return new UserInputParts(
                        text,
                        parts,
                        AttachmentPolicyFailure: AttachmentPolicyException.TooLarge(attachment.Name, attachment.SizeBytes));
                }

                var resourceKey = NormalizeOptional(attachment.AttachmentId);
                if (resourceKey is null)
                {
                    unseenCount++;
                    continue;
                }

                LarkMessageResourceDownloadResult downloaded;
                try
                {
                    downloaded = await larkClient.DownloadMessageResourceAsync(
                            token,
                            new LarkMessageResourceDownloadRequest(
                                messageId,
                                resourceKey,
                                LarkMessageResourceKind.Image),
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
                        "Failed to download Lark image attachment for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey}",
                        providerSlug,
                        messageId,
                        resourceKey);
                    unseenCount++;
                    continue;
                }

                if (!downloaded.Succeeded ||
                    downloaded.Content.Length == 0)
                {
                    _logger.LogWarning(
                        "Lark image attachment download was not usable for chat LLM input: provider={ProviderSlug} messageId={MessageId} resourceKey={ResourceKey} status={Status} detail={Detail}",
                        providerSlug,
                        messageId,
                        resourceKey,
                        downloaded.HttpStatus,
                        downloaded.Detail);
                    unseenCount++;
                    continue;
                }

                if (downloaded.Content.Length > MaxAttachmentMaterializationBytes)
                {
                    return new UserInputParts(
                        text,
                        parts,
                        AttachmentPolicyFailure: AttachmentPolicyException.TooLarge(
                            NormalizeOptional(downloaded.FileName) ?? NormalizeOptional(attachment.Name),
                            downloaded.Content.Length));
                }

                if (!IsSupportedImageMediaType(downloaded.ContentType ?? attachment.ContentType))
                {
                    return new UserInputParts(
                        text,
                        parts,
                        AttachmentPolicyFailure: AttachmentPolicyException.Unsupported(
                            NormalizeOptional(downloaded.FileName) ?? NormalizeOptional(attachment.Name),
                            downloaded.ContentType ?? attachment.ContentType));
                }

                if (_fileIngressPort is null)
                {
                    unseenCount++;
                    continue;
                }

                var mediaType = NormalizeImageMediaType(downloaded.ContentType ?? attachment.ContentType);
                var fileName = NormalizeOptional(downloaded.FileName) ?? NormalizeOptional(attachment.Name);
                WorkflowFileIngressResult ingressResult;
                try
                {
                    ingressResult = await _fileIngressPort.IngestAsync(
                            new WorkflowFileIngressRequest(
                                downloaded.Content,
                                WorkflowFileSourceKind.ChatInput,
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
                catch (WorkflowFileIngressPolicyException ex)
                {
                    return new UserInputParts(
                        text,
                        parts,
                        AttachmentPolicyFailure: ToAttachmentPolicyException(
                            ex,
                            fileName,
                            mediaType,
                            downloaded.Content.Length));
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

        var instruction = unseenCount > 0
            ? BuildAttachmentVisibilityInstruction(
                unseenCount,
                "one or more attachments could not be converted to LLM image input")
            : null;

        return new UserInputParts(text, parts, instruction);
    }

    private static AttachmentPolicyException ToAttachmentPolicyException(
        WorkflowFileIngressPolicyException source,
        string? fallbackFileName,
        string? fallbackMediaType,
        long fallbackSizeBytes) =>
        source.Kind switch
        {
            WorkflowFileIngressPolicyRejectionKind.TooLarge => AttachmentPolicyException.TooLarge(
                source.FileName ?? fallbackFileName,
                source.SizeBytes ?? fallbackSizeBytes),
            WorkflowFileIngressPolicyRejectionKind.UnsupportedMediaType => AttachmentPolicyException.Unsupported(
                source.FileName ?? fallbackFileName,
                source.MediaType ?? fallbackMediaType),
            _ => AttachmentPolicyException.Unsupported(
                source.FileName ?? fallbackFileName,
                source.MediaType ?? fallbackMediaType),
        };

    private async Task<UserInputParts> MaterializeUserInputPartsAsync(UserInputParts input, CancellationToken ct)
    {
        if (!input.Parts.Any(static part => part.FileRef is not null))
            return input;

        var materialized = await MaterializeFileRefPartsAsync(input.Parts, _fileArtifactReadPort, ct)
            .ConfigureAwait(false);
        return input with { Parts = materialized };
    }

    internal static async Task<IReadOnlyList<ContentPart>> MaterializeFileRefPartsAsync(
        IReadOnlyList<ContentPart> parts,
        IWorkflowFileArtifactReadPort? fileArtifactReadPort,
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

            var artifact = await fileArtifactReadPort.OpenReadAsync(ToWorkflowFileRef(part.FileRef), ct)
                .ConfigureAwait(false);
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
                throw AttachmentPolicyException.TooLarge(fileName, buffer.Length + read);

            buffer.Write(chunk, 0, read);
        }
    }

    private static void ValidateMaterializedPartDescriptor(ContentPart part, WorkflowFileRef descriptor)
    {
        var mediaType = descriptor.MediaType ?? part.MediaType;
        if (part.Kind == ContentPartKind.Image && !IsSupportedImageMediaType(mediaType))
            throw AttachmentPolicyException.Unsupported(NormalizeOptional(descriptor.FileName) ?? part.Name, mediaType);
    }

    private static LlmChatFileRef ToChatFileRef(WorkflowFileRef source) =>
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

    private static WorkflowFileRef ToWorkflowFileRef(LlmChatFileRef source) =>
        new()
        {
            FileId = NormalizeOptional(source.FileId),
            ArtifactId = NormalizeOptional(source.ArtifactId),
            SourceKind = ToWorkflowFileSourceKind(source.SourceKind),
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

    private static LlmChatFileSourceKind ToChatFileSourceKind(WorkflowFileSourceKind kind) =>
        kind switch
        {
            WorkflowFileSourceKind.ChatInput => LlmChatFileSourceKind.ChatInput,
            WorkflowFileSourceKind.FormUpload => LlmChatFileSourceKind.FormUpload,
            WorkflowFileSourceKind.ConnectedServiceResource => LlmChatFileSourceKind.ConnectedServiceResource,
            WorkflowFileSourceKind.ExternalResource => LlmChatFileSourceKind.ExternalResource,
            WorkflowFileSourceKind.Generated => LlmChatFileSourceKind.Generated,
            _ => LlmChatFileSourceKind.Unspecified,
        };

    private static WorkflowFileSourceKind ToWorkflowFileSourceKind(LlmChatFileSourceKind kind) =>
        kind switch
        {
            LlmChatFileSourceKind.ChatInput => WorkflowFileSourceKind.ChatInput,
            LlmChatFileSourceKind.FormUpload => WorkflowFileSourceKind.FormUpload,
            LlmChatFileSourceKind.ConnectedServiceResource => WorkflowFileSourceKind.ConnectedServiceResource,
            LlmChatFileSourceKind.ExternalResource => WorkflowFileSourceKind.ExternalResource,
            LlmChatFileSourceKind.Generated => WorkflowFileSourceKind.Generated,
            _ => WorkflowFileSourceKind.Unspecified,
        };

    private ILarkNyxClient? ResolveLarkResourceDownloadClient(ChatActivity activity, out string? providerSlug)
    {
        providerSlug = NormalizeOptional(activity.TransportExtras?.NyxProviderSlug);
        if (providerSlug is not null && _larkOutboundClientFactory is not null)
            return _larkOutboundClientFactory.ResolveNyxClient(providerSlug);

        return _larkClient;
    }

    internal static string BuildControlledAttachmentFailureText(string reason)
    {
        var detail = NormalizeOptional(reason) ?? "the attachment cannot be processed";
        return $"I cannot process this attachment because {detail}. Please upload a supported image under 10 MB and try again.";
    }

    private static async Task<ConversationReplyResult> BuildControlledAttachmentFailureAsync(
        AttachmentPolicyException failure,
        IStreamingReplySink? streamingSink,
        CancellationToken ct)
    {
        var text = BuildControlledAttachmentFailureText(failure.UserVisibleReason);
        if (streamingSink is not null)
            await streamingSink.OnDeltaAsync(text, ct).ConfigureAwait(false);
        return new ConversationReplyResult(
            Text: text,
            Usage: null,
            FinishReason: AttachmentPolicyErrorCode,
            AppendedHistory: [new ConversationHistoryEntry { Role = "assistant", Content = text }]);
    }

    private static AgentRunReplyStepPlan BuildTerminalAttachmentFailureStepPlan(
        AttachmentPolicyException failure,
        IReadOnlyDictionary<string, string> metadata,
        LLMControlContext llmControl,
        AgentToolExecutionContext toolContext,
        int maxToolRounds,
        bool disableTools,
        LLMControlContext? ownerFallbackLlmControl,
        AgentToolExecutionContext? ownerFallbackToolContext)
    {
        var text = BuildControlledAttachmentFailureText(failure.UserVisibleReason);
        var runtime = new ChatRuntime(
            providerFactory: () => new FixedReplyProvider(text, AttachmentPolicyErrorCode),
            history: new global::Aevatar.AI.Core.Chat.ChatHistory(),
            toolLoop: new ToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: () => new LLMRequest
            {
                Messages = [ChatMessage.System("Attachment rejected before provider dispatch.")],
                Metadata = metadata,
                ToolContext = toolContext,
                LlmControl = llmControl,
                RoutingContext = llmControl.ToRoutingContext(),
                Tools = null,
            },
            agentMiddlewares: [],
            llmMiddlewares: []);
        return new AgentRunReplyStepPlan(
            runtime.CreateStepExecutor(),
            metadata,
            llmControl,
            toolContext,
            InitialMessages: [],
            maxToolRounds,
            disableTools,
            ownerFallbackLlmControl,
            ownerFallbackToolContext);
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

    private static bool IsSupportedImageMediaType(string? mediaType)
    {
        var normalized = NormalizeOptional(mediaType)?.ToLowerInvariant();
        return normalized is "image" or "image/png" or "image/jpeg" or "image/jpg" or "image/webp" or "image/gif";
    }

    private static string NormalizeImageMediaType(string? mediaType)
    {
        var normalized = NormalizeOptional(mediaType)?.ToLowerInvariant();
        return normalized switch
        {
            "image/jpg" => "image/jpeg",
            "image/png" or "image/jpeg" or "image/webp" or "image/gif" => normalized,
            _ => "image/png",
        };
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Reasoning content is per-turn working memory, never conversation input: replaying a
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
        entry.ContentParts.AddRange((message.ContentParts ?? []).Select(ContentPartProtoMapper.ToProto));
        entry.ToolCalls.AddRange((message.ToolCalls ?? []).Select(ToConversationToolCallEntry));
        return entry;
    }

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

    private IReadOnlyList<IToolCallMiddleware> BuildToolMiddlewaresForTurn()
    {
        var effective = new List<IToolCallMiddleware>(_toolMiddlewares.Count + 2)
        {
            new ToolCallCredentialPolicyMiddleware(),
            new ToolApprovalMiddleware(_approvalHandler ?? MissingApprovalHandler.Instance),
        };
        effective.AddRange(_toolMiddlewares.Where(static middleware =>
            middleware is not ToolApprovalMiddleware and not ToolCallCredentialPolicyMiddleware));
        return effective;
    }

    private ChatRuntime BuildRuntime(
        ChatActivity activity,
        LLMControlContext llmControl,
        AgentToolExecutionContext toolContext,
        IReadOnlyDictionary<string, string> externalMetadata,
        ToolManager tools,
        string? attachmentVisibilityInstruction)
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
                toolMiddlewares: BuildToolMiddlewaresForTurn(),
                llmMiddlewares: _llmMiddlewares),
            hooks: null,
            requestBuilder: () => new LLMRequest
            {
                Messages =
                [
                    ChatMessage.System(BuildSystemPrompt(externalMetadata, toolContext, attachmentVisibilityInstruction)),
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
                    effectiveControl = effectiveControl with
                    {
                        NyxIdRoutePreference = ownerFallbackControl?.NyxIdRoutePreference,
                    };
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

        if (_userMemoryStore is not null)
        {
            try
            {
                var promptSection = await _userMemoryStore.BuildPromptSectionAsync(2000, ct);
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "User memory prompt context is unavailable; continuing reply generation without user memory.");
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

        var modelApplied = !string.IsNullOrWhiteSpace(preferences.DefaultModel);
        var routeApplied = !string.IsNullOrWhiteSpace(preferences.PreferredRoute);
        var roundsApplied = preferences.MaxToolRounds > 0;
        if (modelApplied || routeApplied || roundsApplied)
        {
            effectiveControl = effectiveControl with
            {
                ModelOverride = modelApplied ? preferences.DefaultModel!.Trim() : effectiveControl.ModelOverride,
                NyxIdRoutePreference = routeApplied ? preferences.PreferredRoute!.Trim() : effectiveControl.NyxIdRoutePreference,
                MaxToolRoundsOverride = roundsApplied ? preferences.MaxToolRounds : effectiveControl.MaxToolRoundsOverride,
            };
        }
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

    private async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(bool isChannelTurn, CancellationToken ct)
    {
        if (_toolSources.Count == 0)
            return [];

        var discovered = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _toolSources)
        {
            var tools = await source.DiscoverToolsAsync(ct);
            foreach (var tool in tools)
            {
                // Channel-side exclusion by GENERIC capability, not by tool name: a tool that
                // declares AgentToolCapabilities.ExcludeFromDirectChannelChat completes its work
                // off-chat (e.g. delivered to /workflow/observatory), so surfacing it on this
                // direct-channel/Lark agent would let the model silently route a chat user's
                // request away from their chat. Such tools stay in the global catalog for the
                // workflow allowlist path; the exclusion is channel-side only. No channel agent
                // depended on these tools, so this changes no existing channel flow.
                if (IsExcludedFromDirectChannelChat(tool))
                    continue;

                // Issue #2580 Item 2: in a channel-relay turn the effective credential is a
                // bot-class relay/API-key token that the broker rejects on human-only surfaces, so a
                // tool self-declaring RequiresHumanSession can only fail. Filter it out of channel
                // turns — never offered to the model, never registered so it cannot be invoked.
                // Console/studio human-session turns keep the full set. Name-agnostic, like above.
                if (isChannelTurn && DeclaresCapability(tool, AgentToolCapabilities.RequiresHumanSession))
                    continue;

                discovered[tool.Name] = tool;
            }
        }

        return discovered.Values.ToArray();
    }

    // A tool is hidden from the direct channel/chat surface when it self-declares the
    // generic ExcludeFromDirectChannelChat capability via IAgentToolCapabilityDescriptor.
    // The channel path never inspects a specific tool name; eligibility is a property of
    // the tool, keeping channel routing agnostic to individual tool/skill identities.
    private static bool IsExcludedFromDirectChannelChat(IAgentTool tool) =>
        DeclaresCapability(tool, AgentToolCapabilities.ExcludeFromDirectChannelChat);

    private static bool DeclaresCapability(IAgentTool tool, string capability) =>
        tool is IAgentToolCapabilityDescriptor descriptor &&
        descriptor.Capabilities.Any(declared =>
            string.Equals(declared, capability, StringComparison.OrdinalIgnoreCase));

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

    private static string AppendSystemPromptSuffix(string prompt, string? suffix) =>
        string.IsNullOrEmpty(suffix) ? prompt : $"{prompt.TrimEnd()}\n\n{suffix}";

    private string BuildSystemPrompt(
        IReadOnlyDictionary<string, string> metadata,
        AgentToolExecutionContext toolContext,
        string? attachmentVisibilityInstruction = null)
    {
        var prompt = LoadBaseSystemPrompt();
        prompt = AppendSystemSkillOverlay(
            prompt,
            ResolveChannelPlatform(toolContext, metadata),
            toolContext.Credentials.NyxIdAccessToken);
        prompt += NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(_relayOptions);
        var channelContext = ChannelContextMiddleware.BuildChannelContextSection(metadata);
        if (!string.IsNullOrWhiteSpace(channelContext))
            prompt += "\n\n" + channelContext;

        if (_localSkillCatalog is not null && _localSkillCatalog.Count > 0)
        {
            var skillSection = _localSkillCatalog.BuildSystemPromptSection();
            if (!string.IsNullOrEmpty(skillSection))
                prompt += "\n" + skillSection;
        }

        if (!string.IsNullOrWhiteSpace(attachmentVisibilityInstruction))
            prompt += "\n\n" + attachmentVisibilityInstruction.Trim();

        return prompt;
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

    private string AppendSystemSkillOverlay(string prompt, string? platform, string? nyxIdAccessToken)
    {
        var overlayMarkdown = _overlayProvider
            ?.GetCurrent(new SystemSkillOverlayRequest(platform, nyxIdAccessToken))
            ?.OverlayMarkdown;
        if (string.IsNullOrWhiteSpace(overlayMarkdown))
            return prompt;

        if (string.IsNullOrWhiteSpace(prompt))
            return overlayMarkdown.Trim();

        return $"{prompt.TrimEnd()}\n\n{overlayMarkdown.Trim()}";
    }

    internal sealed class AttachmentPolicyException : Exception
    {
        private AttachmentPolicyException(string reason, string userVisibleReason)
            : base(userVisibleReason)
        {
            Reason = reason;
            UserVisibleReason = userVisibleReason;
        }

        public string Reason { get; }

        public string UserVisibleReason { get; }

        public static AttachmentPolicyException TooLarge(string? fileName, long sizeBytes) =>
            new(
                "too_large",
                $"{FormatFileName(fileName)}attachment is too large ({sizeBytes} bytes)");

        public static AttachmentPolicyException Unsupported(string? fileName, string? mediaType) =>
            new(
                "unsupported",
                $"{FormatFileName(fileName)}attachment format is not supported ({NormalizeOptional(mediaType) ?? "unknown media type"})");

        private static string FormatFileName(string? fileName)
        {
            var normalized = NormalizeOptional(fileName);
            return normalized is null ? string.Empty : $"'{normalized}' ";
        }
    }

    private sealed class FixedReplyProvider(string text, string finishReason) : ILLMProvider
    {
        public string Name => "controlled-attachment-failure";

        public LLMProviderCapabilities Capabilities => LLMProviderCapabilities.TextOnly;

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk
            {
                DeltaContent = text,
            };
            await Task.CompletedTask.ConfigureAwait(false);
            yield return new LLMStreamChunk
            {
                IsLast = true,
                FinishReason = finishReason,
            };
        }
    }

    private static string LoadBaseSystemPrompt()
    {
        // Kernel source lock: channel replies and NyxIdChatSystemPrompt.Load both read the same
        // embedded system-prompt.md resource; channel-only runtime facts are appended after it.
        var assembly = typeof(NyxIdChatGAgent).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("system-prompt.md", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            return "You are a helpful NyxID assistant.";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return "You are a helpful NyxID assistant.";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
