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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
public sealed class NyxIdConversationReplyGenerator : IAgentRunStepConversationReplyGenerator
{
    private const int MaxToolRounds = 40;
    private const int MaxHistoryMessages = 100;
    private const int MaxInlineImageBytes = 10 * 1024 * 1024;
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

    private sealed record SenderPreferenceApplication(bool AnyApplied, bool RouteApplied);

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
        IToolApprovalHandler? approvalHandler = null,
        ILogger<NyxIdConversationReplyGenerator>? logger = null)
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
        var primaryTools = await BuildTurnToolsAsync(replyPlan.DisableTools, ct);

        try
        {
            return await GenerateWithMetadataAsync(
                    activity,
                    replyPlan.Primary,
                    replyPlan.PrimaryControl,
                    replyPlan.PrimaryToolContext,
                    priorHistory,
                    primaryTools,
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

            var fallbackTools = await BuildTurnToolsAsync(disableTools: true, ct);
            return await GenerateWithMetadataAsync(
                    activity,
                    replyPlan.OwnerFallback,
                    replyPlan.OwnerFallbackControl ?? llmControl ?? LLMControlContext.Empty,
                    replyPlan.OwnerFallbackToolContext,
                    priorHistory,
                    fallbackTools,
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
        var tools = await BuildTurnToolsAsync(disableTools, ct);
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
        var runtime = BuildRuntime(
            activity,
            replyPlan.PrimaryControl,
            effectiveToolContext,
            externalMetadata,
            tools,
            input.AttachmentVisibilityInstruction);

        var initialMessages = new List<ChatMessage>
        {
            ChatMessage.System(BuildSystemPrompt(externalMetadata, input.AttachmentVisibilityInstruction)),
        };
        initialMessages.AddRange((priorHistory ?? []).Select(ToChatMessage));
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
    private async Task<ToolManager> BuildTurnToolsAsync(bool disableTools, CancellationToken ct)
    {
        var tools = new ToolManager();
        if (disableTools)
            return tools;

        foreach (var tool in await DiscoverToolsAsync(ct))
            tools.Register(tool);

        // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
        //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
        //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
        if (_localSkillCatalog is not null || _remoteSkillFetcher is not null)
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

        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: NyxID reply construction passed stream_buffer_capacity into ChatRuntime after the stream loop moved to Task.Run + Channel.
        //   New principle: ChatRuntime owns the async stream directly; this caller only supplies provider, tools, middleware, and request identity.
        var history = new global::Aevatar.AI.Core.Chat.ChatHistory
        {
            MaxMessages = MaxHistoryMessages + Math.Min(priorHistory?.Count ?? 0, MaxHistoryMessages),
        };
        history.AddRange((priorHistory ?? []).Select(ToChatMessage));
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
                    ChatMessage.System(BuildSystemPrompt(effectiveMetadata, input.AttachmentVisibilityInstruction)),
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
        string? AttachmentVisibilityInstruction = null);

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

        if (_larkClient is null)
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

            foreach (var attachment in source.Attachments)
            {
                if (attachment.Kind != AttachmentKind.Image)
                {
                    unseenCount++;
                    continue;
                }

                if (attachment.SizeBytes > MaxInlineImageBytes)
                {
                    unseenCount++;
                    continue;
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
                    downloaded = await _larkClient.DownloadMessageResourceAsync(
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
                        "Failed to download Lark image attachment for chat LLM input: messageId={MessageId} resourceKey={ResourceKey}",
                        messageId,
                        resourceKey);
                    unseenCount++;
                    continue;
                }

                if (!downloaded.Succeeded ||
                    downloaded.Content.Length == 0 ||
                    downloaded.Content.Length > MaxInlineImageBytes ||
                    !IsSupportedImageMediaType(downloaded.ContentType ?? attachment.ContentType))
                {
                    unseenCount++;
                    continue;
                }

                parts.Add(ContentPart.ImagePart(
                    Convert.ToBase64String(downloaded.Content),
                    NormalizeImageMediaType(downloaded.ContentType ?? attachment.ContentType),
                    NormalizeOptional(downloaded.FileName) ?? NormalizeOptional(attachment.Name)));
            }
        }

        var instruction = unseenCount > 0
            ? BuildAttachmentVisibilityInstruction(
                unseenCount,
                "one or more attachments could not be converted to LLM image input")
            : null;

        return new UserInputParts(text, parts, instruction);
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

    private static ChatMessage ToChatMessage(ConversationHistoryEntry entry) =>
        new()
        {
            Role = string.IsNullOrWhiteSpace(entry.Role) ? "user" : entry.Role,
            Content = string.IsNullOrEmpty(entry.Content) ? null : entry.Content,
            ReasoningContent = string.IsNullOrEmpty(entry.ReasoningContent) ? null : entry.ReasoningContent,
            ContentParts = entry.ContentParts.Select(ContentPartProtoMapper.FromProto).ToArray(),
            ToolCallId = string.IsNullOrEmpty(entry.ToolCallId) ? null : entry.ToolCallId,
            ToolCalls = entry.ToolCalls.Select(ToToolCall).ToArray(),
        };

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
        var effective = new List<IToolCallMiddleware>(_toolMiddlewares.Count + 1)
        {
            new ToolApprovalMiddleware(_approvalHandler ?? MissingApprovalHandler.Instance),
        };
        effective.AddRange(_toolMiddlewares);
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
            MaxMessages = MaxHistoryMessages,
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
                    ChatMessage.System(BuildSystemPrompt(externalMetadata, attachmentVisibilityInstruction)),
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

            if (_preferencesStore is not null)
            {
                var preferenceResult = await ApplyPreferencesAsync(senderBindingId, effectiveControl, ct);
                effectiveControl = preferenceResult.Control;
                var applied = preferenceResult.Application;
                if (applied.RouteApplied)
                {
                    if (!string.IsNullOrWhiteSpace(llmControl?.SenderNyxIdAccessToken))
                    {
                        var trimmedToken = llmControl.SenderNyxIdAccessToken.Trim();
                        effectiveControl = effectiveControl with
                        {
                            NyxIdAccessToken = trimmedToken,
                            NyxIdOrgToken = trimmedToken,
                            SenderNyxIdAccessToken = trimmedToken,
                        };
                    }
                    else
                    {
                        effective = ownerSnapshot;
                        effectiveControl = ownerFallbackControl;
                        effectiveToolContext = ownerFallbackToolContext;
                        ownerFallback = null;
                        ownerFallbackControl = null;
                        ownerFallbackToolContext = null;
                    }
                }
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
            catch
            {
                // User memory is best-effort context and must not break the main reply path.
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
            return new SenderPreferenceResult(effectiveControl, new SenderPreferenceApplication(false, false));

        NyxIdUserLlmPreferences preferences;
        try
        {
            preferences = await _preferencesStore.GetForBindingAsync(senderBindingId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SenderPreferenceResult(effectiveControl, new SenderPreferenceApplication(false, false));
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
            new SenderPreferenceApplication(modelApplied || routeApplied || roundsApplied, routeApplied));
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

    private async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct)
    {
        if (_toolSources.Count == 0)
            return [];

        var discovered = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _toolSources)
        {
            var tools = await source.DiscoverToolsAsync(ct);
            foreach (var tool in tools)
                discovered[tool.Name] = tool;
        }

        return discovered.Values.ToArray();
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

    private string BuildSystemPrompt(
        IReadOnlyDictionary<string, string> metadata,
        string? attachmentVisibilityInstruction = null)
    {
        var prompt = LoadBaseSystemPrompt();
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

    private static string LoadBaseSystemPrompt()
    {
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
