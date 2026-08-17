using System.Net.Http;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.SkillInvocations;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Slash;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public sealed class ChannelConversationTurnRunner : IConversationTurnRunner
{
    private static readonly HashSet<string> LocalSlashCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "approve",
        "reject",
        "submit",
        "init",
        "unbind",
        "whoami",
        "model",
        "models",
        "llm",
        "route",
        "agents",
        "agent-status",
        "run-agent",
        "disable-agent",
        "enable-agent",
        "delete-agent",
        "clear",
        "reset",
    };

    private static readonly HashSet<string> ClearHistoryCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "clear",
        "reset",
    };

    private sealed record ResolvedSenderBinding(string BindingId, ExternalSubjectRef Subject, string? OwnerScopeId);

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    private sealed record SlashBindingLookup(
        bool IdentityEnabled,
        bool SubjectResolved,
        ExternalSubjectRef? Subject,
        BindingId? BindingId);

    private sealed record LarkSubjectContactIds(string? UserId, string? EmployeeId);

    private sealed record ReplyChannelContext(
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyList<AgentToolChannelIdentityHint> IdentityHints);

    private readonly IServiceProvider _toolServiceProvider;
    private readonly IAgentToolExecutionPort _toolExecutionPort;
    private readonly IChannelBotRegistrationQueryPort _registrationQueryPort;
    private readonly IChannelBotRegistrationQueryByNyxIdentityPort? _registrationQueryByNyxIdentityPort;
    private readonly IEnumerable<IPlatformAdapter> _platformAdapters;
    private readonly NyxIdApiClient _nyxClient;
    private readonly NyxIdRelayOutboundPort _relayOutboundPort;
    private readonly IInteractiveReplyDispatcher? _interactiveReplyDispatcher;
    private readonly IOwnerLlmConfigSource? _ownerLlmConfigSource;
    private readonly IExternalIdentityBindingQueryPort? _identityBindingQueryPort;
    private readonly ChannelSlashCommandRegistry? _slashCommandRegistry;
    private readonly INyxIdCapabilityBroker? _capabilityBroker;
    private readonly IBindingRevocationReconciler? _bindingRevocationReconciler;
    private readonly IUserLlmSelectionService? _userLlmSelectionService;
    private readonly IUserLlmOptionsService? _userLlmOptionsService;
    private readonly IUserLlmOptionsRenderer<MessageContent>? _userLlmOptionsRenderer;
    private readonly IUserConfigQueryPort? _userConfigQueryPort;
    private readonly ChannelPlatformReplyService? _replyService;
    private readonly ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>? _workflowResumeService;
    private readonly ChannelWorkflowDraftRunAdmission? _workflowDraftRunAdmission;
    private readonly IRemoteToolApprovalPort? _remoteToolApprovalPort;
    private readonly ILogger<ChannelConversationTurnRunner> _logger;
    private readonly ILarkBotIdentityResolver? _botIdentityResolver;
    private readonly INyxIdCurrentUserResolver? _nyxIdCurrentUserResolver;
    private readonly IChannelRelayTailTextSender? _relayTailTextSender;
    private readonly IChannelRelayProxyResponseClassifier? _relayProxyResponseClassifier;
    private readonly IAgentRunToolApprovalDecisionDispatcher? _agentRunToolApprovalDecisionDispatcher;

    public ChannelConversationTurnRunner(
        IServiceProvider services,
        IChannelBotRegistrationQueryPort registrationQueryPort,
        IChannelBotRegistrationQueryByNyxIdentityPort? registrationQueryByNyxIdentityPort,
        IEnumerable<IPlatformAdapter> platformAdapters,
        NyxIdApiClient nyxClient,
        NyxIdRelayOutboundPort relayOutboundPort,
        IInteractiveReplyDispatcher? interactiveReplyDispatcher,
        ILogger<ChannelConversationTurnRunner> logger,
        IAgentToolExecutionPort toolExecutionPort,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        IExternalIdentityBindingQueryPort? identityBindingQueryPort = null,
        ChannelSlashCommandRegistry? slashCommandRegistry = null,
        INyxIdCapabilityBroker? capabilityBroker = null,
        IBindingRevocationReconciler? bindingRevocationReconciler = null,
        IUserLlmSelectionService? userLlmSelectionService = null,
        IUserLlmOptionsService? userLlmOptionsService = null,
        IUserLlmOptionsRenderer<MessageContent>? userLlmOptionsRenderer = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        ChannelPlatformReplyService? replyService = null,
        ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>? workflowResumeService = null,
        ChannelWorkflowDraftRunAdmission? workflowDraftRunAdmission = null,
        IRemoteToolApprovalPort? remoteToolApprovalPort = null,
        ILarkBotIdentityResolver? botIdentityResolver = null,
        INyxIdCurrentUserResolver? nyxIdCurrentUserResolver = null,
        IChannelRelayTailTextSender? relayTailTextSender = null,
        IChannelRelayProxyResponseClassifier? relayProxyResponseClassifier = null,
        IAgentRunToolApprovalDecisionDispatcher? agentRunToolApprovalDecisionDispatcher = null)
    {
        _toolServiceProvider = services ?? throw new ArgumentNullException(nameof(services));
        _toolExecutionPort = toolExecutionPort ?? throw new ArgumentNullException(nameof(toolExecutionPort));
        _registrationQueryPort = registrationQueryPort ?? throw new ArgumentNullException(nameof(registrationQueryPort));
        _registrationQueryByNyxIdentityPort = registrationQueryByNyxIdentityPort;
        _platformAdapters = platformAdapters ?? throw new ArgumentNullException(nameof(platformAdapters));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _relayOutboundPort = relayOutboundPort ?? throw new ArgumentNullException(nameof(relayOutboundPort));
        _interactiveReplyDispatcher = interactiveReplyDispatcher;
        _ownerLlmConfigSource = ownerLlmConfigSource;
        _identityBindingQueryPort = identityBindingQueryPort;
        _slashCommandRegistry = slashCommandRegistry;
        _capabilityBroker = capabilityBroker;
        _bindingRevocationReconciler = bindingRevocationReconciler;
        _userLlmSelectionService = userLlmSelectionService;
        _userLlmOptionsService = userLlmOptionsService;
        _userLlmOptionsRenderer = userLlmOptionsRenderer;
        _userConfigQueryPort = userConfigQueryPort;
        _replyService = replyService;
        _workflowResumeService = workflowResumeService;
        _workflowDraftRunAdmission = workflowDraftRunAdmission;
        _remoteToolApprovalPort = remoteToolApprovalPort;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _botIdentityResolver = botIdentityResolver;
        _nyxIdCurrentUserResolver = nyxIdCurrentUserResolver;
        _relayTailTextSender = relayTailTextSender;
        _relayProxyResponseClassifier = relayProxyResponseClassifier;
        _agentRunToolApprovalDecisionDispatcher = agentRunToolApprovalDecisionDispatcher;
    }

    public async Task<ConversationTurnResult> RunInboundAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var registration = await ResolveRegistrationAsync(activity, ct);
        if (registration is null)
            return ConversationTurnResult.PermanentFailure("registration_not_found", "Channel registration not found.");

        // Group/channel chats deliver every message once the bot holds the broad read scope, so a
        // turn only "addresses" the bot when it @-mentions the bot, replies to one of the bot's own
        // messages, or is a slash command. Decided before the typing reaction so unaddressed group
        // chatter produces neither a reaction nor a reply.
        if (await ShouldIgnoreUnaddressedGroupMessageAsync(activity, registration, runtimeContext, ct))
            return ConversationTurnResult.Ignored("group_message_not_addressed", activity.Id);

        // Capture the typing-reaction Task instead of `_ =`-discarding it. The direct-reply
        // AgentBuilder path can complete fast enough that the clear fires before Lark has
        // persisted the typing reaction; the clear GET would then find nothing to delete and
        // leave Typing on the message. Threading the task to the clear site lets the clear
        // await-with-timeout the typing POST first. The deferred-LLM and streaming
        // paths don't get this task (different invocation), but their natural latency is
        // orders of magnitude greater than the typing POST so the race cannot fire.
        var typingReactionTask = TrySendImmediateLarkReactionAsync(activity, registration, ct);

        var inbound = ToInboundMessage(activity);
        var hasSlashCommand = TryParseSlashCommand(inbound.Text, out var observedCommandName, out _);
        _logger.LogInformation(
            "Channel inbound routing started: activity={ActivityId}, type={ActivityType}, platform={Platform}, chatType={ChatType}, conversation={CanonicalKey}, hasText={HasText}, slashCommand={SlashCommand}, hasRelayDelivery={HasRelayDelivery}",
            activity.Id,
            activity.Type,
            inbound.Platform,
            inbound.ChatType,
            activity.Conversation?.CanonicalKey,
            !string.IsNullOrWhiteSpace(inbound.Text),
            hasSlashCommand ? observedCommandName : string.Empty,
            HasRelayDelivery(inbound));
        // Workflow resume is the structured-payload path (card_action etc) and
        // takes priority over slash-command parsing — a card-action with text
        // that looks like /init is still a card-action. (deepseek-v4-pro L65)
        if (await TryHandleWorkflowResumeAsync(inbound, ct) is { } workflowResumeResult)
            return workflowResumeResult;

        if (await TryHandleAgentRunToolApprovalCardActionAsync(
                    activity,
                    inbound,
                    registration,
                    runtimeContext,
                    ct)
                .ConfigureAwait(false) is { } agentRunApprovalResult)
        {
            return agentRunApprovalResult;
        }

        if (await TryHandleNyxIdApprovalCardActionAsync(activity, inbound, registration, runtimeContext, ct)
                .ConfigureAwait(false) is { } nyxIdApprovalResult)
        {
            return nyxIdApprovalResult;
        }

        var inboundEvent = ToInboundEvent(activity, registration, inbound);

        if (activity.Type != ActivityType.CardAction &&
            await TryHandleWorkflowDraftRunAsync(
                activity,
                inbound,
                registration,
                inboundEvent,
                runtimeContext,
                ct).ConfigureAwait(false) is { } workflowDraftRunResult)
        {
            return workflowDraftRunResult;
        }

        if (activity.Type != ActivityType.CardAction &&
            await TryHandleSlashCommandAsync(activity, inbound, registration, runtimeContext, ct) is { } slashResult)
            return slashResult;

        // Normal LLM messages do not force /init. If the sender is bound we
        // carry that binding forward so the reply generator can try the
        // sender's own NyxID LLM prefs first; otherwise the run actor/generator
        // will use the bot owner's ambient LLM config.
        var senderBinding = await TryResolveSenderBindingAsync(inbound, registration, ct).ConfigureAwait(false);

        if (await TryHandleLlmSelectionCardActionAsync(activity, inbound, registration, runtimeContext, senderBinding?.BindingId, ct).ConfigureAwait(false) is { } llmSelectionResult)
            return llmSelectionResult;

        if (await TryHandleAgentBuilderAsync(activity, inboundEvent, registration, runtimeContext, senderBinding, typingReactionTask, ct) is { } agentBuilderResult)
            return agentBuilderResult;

        if (activity.Type == ActivityType.CardAction)
        {
            if (TryBuildGenericFormSubmitLlmText(activity.Content?.CardAction, out var formSubmitText))
            {
                var formInbound = WithText(inbound, formSubmitText);
                var formSubmitInboundEvent = ToInboundEvent(
                    activity,
                    registration,
                    formInbound);
                return ConversationTurnResult.LlmReplyRequested(
                    await BuildLlmReplyRequestAsync(
                            activity,
                            registration,
                            formSubmitInboundEvent,
                            runtimeContext,
                            senderBinding,
                            ct)
                        .ConfigureAwait(false));
            }

            // Generic reply_with_interaction buttons mirror the form_submit path: the
            // click is the user's answer to the LLM's own card, so it continues the
            // conversation as an LLM turn. Typed payloads (workflow resume, LLM
            // selection, agent builder) were already consumed by their routers above.
            if (TryBuildGenericButtonClickLlmText(activity.Content?.CardAction, out var buttonClickText))
            {
                var buttonInbound = WithText(inbound, buttonClickText);
                var buttonInboundEvent = ToInboundEvent(
                    activity,
                    registration,
                    buttonInbound);
                return ConversationTurnResult.LlmReplyRequested(
                    await BuildLlmReplyRequestAsync(
                            activity,
                            registration,
                            buttonInboundEvent,
                            runtimeContext,
                            senderBinding,
                            ct)
                        .ConfigureAwait(false));
            }

            // A card_action that survived both routers has no actionable meaning for this
            // bot: promoting it into an LLM turn would send a blank user message and waste
            // a model call. Return a no-reply completion instead of falling through.
            _logger.LogInformation(
                "Ignoring unrecognized card_action inbound: activity={ActivityId}, conversation={CanonicalKey}, actionId={ActionId}",
                activity.Id,
                activity.Conversation?.CanonicalKey,
                activity.Content?.CardAction?.ActionId);
            return ConversationTurnResult.Ignored(
                "unrecognized_card_action",
                activity.Id,
                "Card action payload did not match workflow resume or agent-builder routing.");
        }

        if (string.IsNullOrWhiteSpace(activity.Conversation?.CanonicalKey))
        {
            return ConversationTurnResult.PermanentFailure(
                "conversation_not_found",
                "Conversation routing target is missing.");
        }

        return ConversationTurnResult.LlmReplyRequested(
            await BuildLlmReplyRequestAsync(
                    activity,
                    registration,
                    inboundEvent,
                    runtimeContext,
                    senderBinding,
                    ct,
                    allowDefaultSkillRouting: true)
                .ConfigureAwait(false));
    }

    public Task<ConversationTurnResult> RunInboundAsync(ChatActivity activity, CancellationToken ct) =>
        RunInboundAsync(activity, ConversationTurnRuntimeContext.Empty, ct);

    // ─── Slash command dispatch ───
    //
    // Slash commands (/init, /unbind, /whoami, /model, ...) are routed before
    // the LLM so binding/configuration commands can own their per-user
    // semantics without being swallowed by the chat model. Handlers
    // are discovered as IEnumerable<IChannelSlashCommandHandler> from DI;
    // identity ports are constructor-injected as optional capabilities so
    // deployments that have not enabled binding fall through to the legacy
    // flow. Phase 6 (issue #513):
    // each handler declares RequiresBinding so unbound senders trying to use
    // a binding-only command (e.g. /model use) get a binding hint instead of
    // a stack trace; normal LLM turns still have owner fallback.
    private async Task<ConversationTurnResult?> TryHandleSlashCommandAsync(
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        if (!TryParseSlashCommand(inbound.Text, out var commandName, out var argumentText))
            return null;

        // /clear is a conversation-state command, not a user-identity command: the
        // retained transcript window belongs to the conversation actor running this
        // turn, so it is handled here (no binding required) and the actor applies the
        // typed clear outcome through its own committed domain event.
        if (ClearHistoryCommands.Contains(commandName))
            return await HandleClearHistoryCommandAsync(activity, inbound, registration, runtimeContext, ct).ConfigureAwait(false);

        var queryPort = _identityBindingQueryPort;
        if (queryPort is null)
        {
            _logger.LogDebug(
                "Slash command observed but identity query port is not registered; falling through: command={Command}",
                commandName);
            return null;
        }

        var handler = ResolveSlashCommandHandler(commandName);
        var bindingLookup = await ResolveSlashBindingAsync(commandName, inbound, registration, queryPort, ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Slash command routing checked: activity={ActivityId}, command={Command}, handlerFound={HandlerFound}, requiresBinding={RequiresBinding}, identityEnabled={IdentityEnabled}, subjectResolved={SubjectResolved}, bindingFound={BindingFound}",
            activity.Id,
            commandName,
            handler is not null,
            handler?.RequiresBinding ?? false,
            bindingLookup.IdentityEnabled,
            bindingLookup.SubjectResolved,
            bindingLookup.BindingId is not null);

        if (handler is null)
        {
            if (bindingLookup.IdentityEnabled && bindingLookup.SubjectResolved && bindingLookup.BindingId is null)
            {
                _logger.LogInformation(
                    "Unknown slash command routed to binding prompt: activity={ActivityId}, command={Command}",
                    activity.Id,
                    commandName);
                return await SendBindingPromptAsync(activity, inbound, registration, runtimeContext, ct).ConfigureAwait(false);
            }

            // Unknown slash command for bound senders falls through to the Ornn
            // skill-discovery rewrite in BuildLlmReplyRequestAsync.
            _logger.LogInformation(
                "Unknown slash command falling through to LLM skill recovery: activity={ActivityId}, command={Command}, bindingFound={BindingFound}",
                activity.Id,
                commandName,
                bindingLookup.BindingId is not null);
            return null;
        }

        if (handler.RequiresBinding && bindingLookup.BindingId is null)
        {
            _logger.LogInformation(
                "Registered slash command routed to binding prompt: activity={ActivityId}, command={Command}",
                activity.Id,
                commandName);
            return await SendBindingPromptAsync(activity, inbound, registration, runtimeContext, ct).ConfigureAwait(false);
        }

        if (bindingLookup.Subject is null)
            return null;

        var commandContext = new ChannelSlashCommandContext
        {
            CommandName = handler.Name,
            ArgumentText = argumentText,
            Subject = bindingLookup.Subject,
            BindingIdValue = bindingLookup.BindingId?.Value,
            RegistrationId = registration.Id,
            RegistrationScopeId = registration.ScopeId ?? string.Empty,
            SenderId = inbound.SenderId.Trim(),
            SenderName = (inbound.SenderName ?? string.Empty).Trim(),
            IsPrivateChat = IsPrivateChat(inbound),
        };

        MessageContent? reply;
        try
        {
            reply = await handler.HandleAsync(commandContext, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slash command {Command} threw", handler.Name);
            reply = new MessageContent { Text = $"处理 /{handler.Name} 时遇到内部错误,请稍后重试。" };
        }

        if (reply is null)
            return null;

        var sentSeed = string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id;
        return await SendReplyAsync(
            reply,
            sentSeed,
            activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct).ConfigureAwait(false);
    }

    private async Task<ConversationTurnResult> HandleClearHistoryCommandAsync(
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        // Group transcripts are shared context owned by every participant; a single
        // member must not wipe them. DM transcripts belong to the sender alone.
        if (!IsPrivateChat(inbound))
        {
            return await SendReplyAsync(
                "/clear 仅支持单聊会话:群聊上下文由全体成员共享,不能由单个成员清空。",
                activity,
                inbound,
                registration,
                runtimeContext,
                ct).ConfigureAwait(false);
        }

        var sent = await SendReplyAsync(
            "✅ 已清空本会话的对话记忆,后续对话将从干净的上下文开始。",
            activity,
            inbound,
            registration,
            runtimeContext,
            ct).ConfigureAwait(false);
        // The flag rides back even when the confirmation send failed: the user asked
        // for the wipe, and the conversation actor owns (and commits) that outcome.
        return sent with { RetainedHistoryClearRequested = true };
    }

    private static bool TryParseSlashCommand(string? text, out string commandName, out string argumentText)
    {
        commandName = string.Empty;
        argumentText = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Trim handles Unicode leading/trailing whitespace (NBSP / U+3000 /
        // ZWSP) by default. The bigger concern is the *separator* between
        // command and arg: Lark / WeChat clients commonly inject NBSP or
        // ideographic space there, so splitting only on ASCII ' ' would let
        // "/init　foo" miss the registry. Iterate char-by-char and split
        // on the first run of any char.IsWhiteSpace.
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '/')
            return false;

        var firstSeparator = -1;
        for (var i = 1; i < trimmed.Length; i++)
        {
            if (char.IsWhiteSpace(trimmed[i]))
            {
                firstSeparator = i;
                break;
            }
        }

        if (firstSeparator < 0)
        {
            commandName = trimmed[1..].ToLowerInvariant();
        }
        else
        {
            commandName = trimmed[1..firstSeparator].ToLowerInvariant();
            argumentText = trimmed[(firstSeparator + 1)..].Trim();
        }

        return commandName.Length > 0;
    }

    private IChannelSlashCommandHandler? ResolveSlashCommandHandler(string commandName)
    {
        // Registry construction validates duplicate Name/Aliases registrations
        // fail-fast at startup. When deployments do not enable slash commands,
        // the optional registry is absent and slash commands fall through.
        return _slashCommandRegistry?.Find(commandName);
    }

    private async Task<ConversationTurnResult> SendBindingPromptAsync(
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        MessageContent reply;
        if (!IsPrivateChat(inbound))
        {
            reply = new MessageContent { Text = "请与 bot 私聊任意消息以获取 NyxID 绑定卡片。" };
        }
        else
        {
            var broker = _capabilityBroker;
            if (broker is null)
            {
                _logger.LogError("Binding gate cannot start NyxID binding because INyxIdCapabilityBroker is not registered.");
                reply = new MessageContent { Text = "NyxID 绑定入口暂不可用,请稍后重试。" };
            }
            else if (!TryResolveExternalSubject(inbound, registration, out var subject))
            {
                _logger.LogWarning(
                    "Binding gate cannot start NyxID binding because subject cannot be resolved: platform={Platform}, sender={Sender}, registration={RegistrationId}",
                    inbound.Platform,
                    inbound.SenderId,
                    registration.Id);
                reply = new MessageContent { Text = "无法识别当前 Lark 用户身份,请稍后重试。" };
            }
            else
            {
                try
                {
                    var challenge = await broker.StartExternalBindingAsync(subject, ct).ConfigureAwait(false);
                    reply = InitChannelSlashCommandHandler.BuildBindingCard(
                        challenge.AuthorizeUrl,
                        challenge.RenewsExistingBinding);
                }
                catch (AevatarOAuthClientNotProvisionedException ex)
                {
                    _logger.LogInformation(
                        ex,
                        "Binding gate observed before aevatar OAuth client bootstrap finished; subject={Platform}:{Tenant}:{Sender}",
                        subject.Platform,
                        subject.Tenant,
                        subject.ExternalUserId);
                    reply = new MessageContent { Text = "Aevatar 正在初始化 NyxID 客户端,请 30 秒后再次发送消息。" };
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Binding gate failed to start external binding for subject={Platform}:{Tenant}:{Sender}",
                        subject.Platform,
                        subject.Tenant,
                        subject.ExternalUserId);
                    reply = new MessageContent { Text = "启动 NyxID 绑定时遇到内部错误,请稍后重试。" };
                }
            }
        }

        var sentSeed = string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id;
        return await SendReplyAsync(
            reply,
            sentSeed,
            activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct).ConfigureAwait(false);
    }

    private static bool TryResolveExternalSubject(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        out ExternalSubjectRef subject)
    {
        subject = new ExternalSubjectRef();
        if (string.IsNullOrWhiteSpace(inbound.SenderId) || string.IsNullOrWhiteSpace(inbound.Platform))
            return false;

        var tenant = ResolveTenant(inbound, registration);
        if (tenant is null)
            return false;

        subject = new ExternalSubjectRef
        {
            Platform = inbound.Platform.Trim().ToLowerInvariant(),
            Tenant = tenant,
            ExternalUserId = inbound.SenderId.Trim(),
        };
        return true;
    }

    // Normal LLM messages are allowed to use the bot owner's LLM config when
    // the sender has no NyxID binding. Binding is only required by commands
    // that configure or inspect per-user state (/models, /model use, ...).
    //
    // A lookup FAILURE deliberately degrades to null (owner config, tools off)
    // instead of failing the turn, even though null also trips the issue-#1318
    // unbound-sender tool gate. Failing the turn was tried and is worse on the
    // relay path: the deferred inbound-turn retry rebuilds its runtime context
    // without the runtime-only reply token and terminally fails with
    // missing_runtime_reply_token before ever re-invoking the runner, and letting
    // the exception propagate drops the message outright (durable envelope retry
    // refuses credential-carrying relay events). Typed transient classification
    // is also unreliable at this seam — the production ES-backed reader surfaces
    // 5xx/429 as InvalidOperationException. So: reply now on owner config, and
    // the reply generator's tools-disabled system-prompt notice keeps the model
    // honest about the missing tool surface instead of it denying the capability
    // exists. Real cancellation still propagates.
    private async Task<ResolvedSenderBinding?> TryResolveSenderBindingAsync(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        var queryPort = _identityBindingQueryPort;
        if (queryPort is null)
            return null;

        if (!TryResolveExternalSubject(inbound, registration, out var subject))
            return null;

        BindingId? existing;
        try
        {
            existing = await queryPort.ResolveAsync(subject, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsTransientBindingLookupFailure(ex))
        {
            // Transient infra failures (readmodel blip, transient HTTP/timeout, JSON
            // shape mismatch from upstream): degrade to owner credentials and keep
            // the conversation alive.
            _logger.LogWarning(
                ex,
                "Transient sender NyxID binding lookup failure; falling back to bot owner LLM config with tools disabled. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return null;
        }
        catch (Exception ex)
        {
            // Non-transient shape (includes the ES reader's InvalidOperationException
            // wrapping of 5xx/429): surface at Error level so ops can distinguish from
            // "sender just isn't bound" — but still fall through to owner credentials
            // so the user gets an honest degraded reply rather than nothing.
            _logger.LogError(
                ex,
                "Sender NyxID binding lookup raised non-transient exception; falling back to bot owner LLM config with tools disabled. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return null;
        }

        if (existing is not null)
        {
            var ownerScopeId = await TryResolveOwnerScopeIdAsync(subject, ct).ConfigureAwait(false);
            return new ResolvedSenderBinding(existing.Value, subject.Clone(), ownerScopeId);
        }

        return null;
    }

    private async Task<string?> TryResolveOwnerScopeIdAsync(ExternalSubjectRef subject, CancellationToken ct)
    {
        var resolver = _toolServiceProvider.GetService<IOwnerScopeResolver>();
        if (resolver is null)
            return null;

        try
        {
            return NormalizeOptional((await resolver.ResolveAsync(subject, ct).ConfigureAwait(false))?.Value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsTransientBindingLookupFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Transient owner scope lookup failure; bound sender tools will run without shared owner scope. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Owner scope lookup raised non-transient exception; bound sender tools will run without shared owner scope. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return null;
        }
    }

    /// <summary>
    /// Distinguish infra-shaped binding lookup failures (worth a Warning + owner fallback)
    /// from logic/programmer errors (worth an Error log so ops sees them). Both degrade to
    /// the owner-config reply; see <see cref="TryResolveSenderBindingAsync"/> for why the
    /// turn must not fail here.
    /// </summary>
    private static bool IsTransientBindingLookupFailure(Exception ex) =>
        ex is HttpRequestException
            or TimeoutException
            or TaskCanceledException
            or System.Text.Json.JsonException
            or System.IO.IOException;

    // Lark-aware private-chat detection. Other platforms map their direct-
    // message chat-type strings here as the runner gains support for them.
    private static bool IsPrivateChat(InboundMessage inbound)
    {
        var chatType = (inbound.ChatType ?? string.Empty).Trim().ToLowerInvariant();
        return chatType is "p2p" or "private" or "direct" or "dm";
    }

    private static string? ResolveTenant(InboundMessage inbound, ChannelBotRegistrationEntry registration)
    {
        // Platform adapters set `open_tenant_id` (Lark) or `tenant` (generic)
        // in InboundMessage.Extra when the inbound carries a typed tenant.
        if (inbound.Extra.TryGetValue("open_tenant_id", out var openTenant) && !string.IsNullOrWhiteSpace(openTenant))
            return openTenant.Trim();
        if (inbound.Extra.TryGetValue("tenant", out var tenant) && !string.IsNullOrWhiteSpace(tenant))
            return tenant.Trim();

        // Fall back to the bot's registration scope id so bindings stay at
        // least per-bot-scoped — each registration is bound to a single
        // tenant on the NyxID side. This is a pragmatic safety net that
        // avoids cross-bot collapse; production adapters should populate the
        // typed tenant key above so the binding scope matches platform
        // semantics exactly.
        if (!string.IsNullOrWhiteSpace(registration.ScopeId))
            return registration.ScopeId.Trim();

        return null;
    }

    private async Task<ConversationTurnResult?> TryHandleLlmSelectionCardActionAsync(
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        string? senderBindingId,
        CancellationToken ct)
    {
        if (activity.Type != ActivityType.CardAction)
            return null;
        if (!TryResolveLlmSelectionAction(activity.Content?.CardAction, inbound, out var llmAction))
            return null;

        MessageContent reply;
        if (string.IsNullOrWhiteSpace(senderBindingId))
        {
            if (_identityBindingQueryPort is null)
            {
                reply = new MessageContent { Text = "当前部署未启用模型偏好,此操作暂不可用。" };
            }
            else
            {
                return await SendBindingPromptAsync(activity, inbound, registration, runtimeContext, ct).ConfigureAwait(false);
            }
        }
        else if (!TryResolveExternalSubject(inbound, registration, out var subject))
        {
            reply = new MessageContent { Text = "无法识别当前用户身份,请稍后重试 /models。" };
        }
        else
        {
            var selectionService = _userLlmSelectionService;
            var optionsService = _userLlmOptionsService;
            var renderer = _userLlmOptionsRenderer;
            if (selectionService is null || optionsService is null || renderer is null)
            {
                reply = new MessageContent { Text = "当前部署未启用模型偏好,此操作暂不可用。" };
            }
            else
            {
                var bindingId = new BindingId { Value = senderBindingId.Trim() };
                var selectionContext = new UserLlmSelectionContext(
                    bindingId.Clone(),
                    subject.Clone(),
                    registration.ScopeId ?? string.Empty);
                var query = new UserLlmOptionsQuery(
                    bindingId.Clone(),
                    subject.Clone(),
                    registration.ScopeId ?? string.Empty);

                reply = await ExecuteLlmSelectionCardActionAsync(
                        llmAction,
                        selectionContext,
                        query,
                        selectionService,
                        optionsService,
                        renderer,
                        ct)
                    .ConfigureAwait(false);
            }
        }

        return await SendReplyAsync(
            reply,
            string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id,
            activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct).ConfigureAwait(false);
    }

    private async Task<MessageContent> ExecuteLlmSelectionCardActionAsync(
        ResolvedLlmSelectionAction action,
        UserLlmSelectionContext selectionContext,
        UserLlmOptionsQuery query,
        IUserLlmSelectionService selectionService,
        IUserLlmOptionsService optionsService,
        IUserLlmOptionsRenderer<MessageContent> renderer,
        CancellationToken ct)
    {
        try
        {
            if (string.Equals(action.Action, TextUserLlmOptionsRenderer.SelectServiceAction, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(action.Value))
                    return new MessageContent { Text = "缺少要切换的 LLM service,请重新发送 /models。" };

                var picked = (await optionsService.GetOptionsAsync(query, ct).ConfigureAwait(false))
                    .Available.FirstOrDefault(option =>
                        option.Identity is
                        {
                            Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
                        } identity &&
                        string.Equals(identity.NyxIdUserServiceId, action.Value.Trim(), StringComparison.Ordinal));
                await selectionService.SetByServiceAsync(
                        selectionContext,
                        action.Value.Trim(),
                        new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault },
                        ct)
                    .ConfigureAwait(false);
                return picked is null
                    ? new MessageContent { Text = "LLM 选择更新已提交；观察到更新后的设置后生效。" }
                    : renderer.RenderSelectionConfirm(
                        picked,
                        new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault });
            }

            if (string.Equals(action.Action, TextUserLlmOptionsRenderer.ApplyPresetAction, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(action.Value))
                    return new MessageContent { Text = "缺少要应用的 LLM preset,请重新发送 /models。" };

                await selectionService.ApplyPresetAsync(selectionContext, action.Value.Trim(), ct).ConfigureAwait(false);
                return new MessageContent
                {
                    Text = $"LLM preset **{action.Value.Trim()}** 更新已提交；观察到更新后的设置后生效。",
                };
            }

            if (string.Equals(action.Action, TextUserLlmOptionsRenderer.ListPageAction, StringComparison.Ordinal))
            {
                var updated = await optionsService.GetOptionsAsync(query, ct).ConfigureAwait(false);
                return renderer.RenderOptions(updated, action.DisplayMode, action.Page);
            }

            return new MessageContent { Text = "这张模型设置卡片已失效，请重新发送 /models 获取最新选项。" };
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            return new MessageContent { Text = "NyxID 客户端正在初始化,请稍后重试 /models。" };
        }
        catch (BindingNotFoundException)
        {
            return new MessageContent { Text = "当前 NyxID 绑定不可用,请先发送 /init 重新绑定。" };
        }
        catch (BindingRevokedException)
        {
            return new MessageContent { Text = "当前 NyxID 绑定已失效,请先发送 /init 重新绑定。" };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return new MessageContent { Text = ex.Message };
        }
    }

    private async Task<ConversationTurnResult?> TryHandleAgentRunToolApprovalCardActionAsync(
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        if (activity.Type != ActivityType.CardAction)
            return null;

        var payload = activity.Content?.CardAction?.AgentRunApproval;
        if (payload is null)
            return null;

        if (!AgentRunId.TryParse(payload.RunId, out _) ||
            string.IsNullOrWhiteSpace(payload.ApprovalRequestId) ||
            string.IsNullOrWhiteSpace(payload.ToolCallId) ||
            string.IsNullOrWhiteSpace(payload.ToolName) ||
            string.IsNullOrWhiteSpace(payload.ArgumentsSha256) ||
            string.IsNullOrWhiteSpace(inbound.SenderId) ||
            string.IsNullOrWhiteSpace(registration.ScopeId) ||
            string.IsNullOrWhiteSpace(activity.Conversation?.CanonicalKey))
        {
            return ConversationTurnResult.PermanentFailure(
                "agent_run_tool_approval_callback_invalid",
                "The AgentRun tool approval callback is missing an exact typed identity.");
        }

        var relayDelivery = activity.OutboundDelivery;
        var replyToken = relayDelivery is null
            ? null
            : ResolveRelayReplyToken(relayDelivery, runtimeContext);
        if (replyToken is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "agent_run_tool_approval_reply_token_missing",
                "The AgentRun tool approval callback reply credential is missing or expired.");
        }

        var dispatcher = _agentRunToolApprovalDecisionDispatcher;
        if (dispatcher is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "agent_run_tool_approval_dispatcher_unavailable",
                "The AgentRun tool approval decision dispatcher is unavailable.");
        }

        var callbackActivity = activity.Clone();
        callbackActivity.TransportExtras ??= new TransportExtras();
        callbackActivity.TransportExtras.NyxRegistrationScopeId = registration.ScopeId.Trim();
        var userAccessToken = ResolveUserAccessToken(activity, runtimeContext);
        if (userAccessToken is not null)
            callbackActivity.TransportExtras.NyxUserAccessToken = userAccessToken;

        var request = new NeedsLlmReplyEvent
        {
            RunId = payload.RunId.Trim(),
            CorrelationId = activity.Id,
            RegistrationId = registration.Id,
            Activity = callbackActivity,
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyToken = replyToken,
            ReplyTokenExpiresAtUnixMs = runtimeContext.NyxRelayReplyToken?.ExpiresAtUtc.ToUnixTimeMilliseconds() ?? 0,
        };
        var command = new AgentRunToolApprovalDecisionRequested
        {
            RunId = payload.RunId.Trim(),
            ApprovalRequestId = payload.ApprovalRequestId.Trim(),
            ToolCallId = payload.ToolCallId.Trim(),
            ToolName = payload.ToolName.Trim(),
            ArgumentsSha256 = payload.ArgumentsSha256.Trim(),
            Approved = payload.Approved,
            SenderId = inbound.SenderId.Trim(),
            RegistrationScopeId = registration.ScopeId.Trim(),
            ConversationKey = activity.Conversation!.CanonicalKey.Trim(),
            Request = request,
        };

        try
        {
            await dispatcher.DispatchAsync(command, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AgentRun tool approval callback dispatch failed: runId={RunId} approvalRequest={ApprovalRequestId} approved={Approved}",
                command.RunId,
                command.ApprovalRequestId,
                command.Approved);
            return ConversationTurnResult.TransientFailure(
                "agent_run_tool_approval_dispatch_failed",
                "The AgentRun tool approval decision could not be dispatched.");
        }

        return ConversationTurnResult.Ignored(
            "agent_run_tool_approval_decision_dispatched",
            activity.Id);
    }

    private async Task<ConversationTurnResult?> TryHandleNyxIdApprovalCardActionAsync(
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        if (activity.Type != ActivityType.CardAction)
            return null;

        var payload = activity.Content?.CardAction?.NyxIdApproval;
        if (payload is null)
            return null;

        var reply = await ExecuteNyxIdApprovalCardActionAsync(activity, runtimeContext, payload, ct)
            .ConfigureAwait(false);

        return await SendReplyAsync(
                reply,
                string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id,
                activity.Conversation,
                inbound,
                registration,
                runtimeContext,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<MessageContent> ExecuteNyxIdApprovalCardActionAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext,
        NyxIdApprovalActionPayload payload,
        CancellationToken ct)
    {
        var requestId = NormalizeOptional(payload.RequestId);
        if (requestId is null)
            return new MessageContent { Text = "Approval action is missing the NyxID request id." };

        var token = ResolveUserAccessToken(activity, runtimeContext);
        if (token is null)
        {
            return new MessageContent
            {
                Text = "Approval action cannot be decided because the NyxID user credential is missing or expired. Open the current approval list and decide it there.",
            };
        }

        var port = _remoteToolApprovalPort;
        if (port is null)
        {
            return new MessageContent
            {
                Text = "Approval action cannot be decided because the remote approval decision service is unavailable.",
            };
        }

        RemoteToolApprovalDecisionResult decision;
        try
        {
            using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
            {
                Credentials = AgentToolCredentials.Empty with { NyxIdAccessToken = token },
            });
            decision = await port.DecideAsync(
                    new RemoteToolApprovalDecision(requestId, payload.Approved),
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
                "NyxID approval card decision failed before response: request={RequestId}, approved={Approved}",
                requestId,
                payload.Approved);
            return new MessageContent { Text = $"Approval decision failed before reaching NyxID: {ex.Message}" };
        }

        if (!decision.Succeeded &&
            TryResolveNyxIdApprovalDecisionError(decision, out var errorText))
        {
            return new MessageContent { Text = errorText };
        }

        return new MessageContent
        {
            Text = payload.Approved
                ? $"Approval request `{requestId}` approved."
                : $"Approval request `{requestId}` rejected.",
        };
    }

    private static bool TryResolveNyxIdApprovalDecisionError(
        RemoteToolApprovalDecisionResult result,
        out string message)
    {
        if (result.Succeeded)
        {
            message = string.Empty;
            return false;
        }

        var detail = NormalizeOptional(result.Detail) ?? "NyxID rejected the approval decision.";
        var key = NormalizeOptional(result.ErrorKey) ?? string.Empty;
        message = key switch
        {
            "already_decided" => "Approval request was already decided.",
            "not_found" => "Approval request was not found or is no longer available.",
            "forbidden" => BuildForbiddenApprovalMessage(detail),
            "unauthorized" or "authentication_failed" => "Approval decision requires a valid NyxID user credential. Please sign in again and retry.",
            _ when result.Status is 401 => "Approval decision requires a valid NyxID user credential. Please sign in again and retry.",
            _ when result.Status is 403 => BuildForbiddenApprovalMessage(detail),
            _ when result.Status is 404 => "Approval request was not found or is no longer available.",
            _ when result.Status is 409 => "Approval request was already decided.",
            _ => $"Approval decision was rejected by NyxID: {detail}",
        };
        return true;
    }

    private static string BuildForbiddenApprovalMessage(string detail) =>
        detail.Contains("expired", StringComparison.OrdinalIgnoreCase)
            ? "Approval request expired."
            : $"Approval decision is not allowed for the current NyxID user: {detail}";

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    private async Task<SlashBindingLookup> ResolveSlashBindingAsync(
        string commandName,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        IExternalIdentityBindingQueryPort queryPort,
        CancellationToken ct)
    {
        if (!TryResolveExternalSubject(inbound, registration, out var subject))
        {
            _logger.LogWarning(
                "Slash command rejected: cannot resolve subject for command={Command}, platform={Platform}, sender={Sender}, registration={RegistrationId}",
                commandName,
                inbound.Platform,
                inbound.SenderId,
                registration.Id);
            return new SlashBindingLookup(true, false, null, null);
        }

        BindingId? existing;
        try
        {
            existing = await queryPort.ResolveAsync(subject, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail closed: if we can't tell whether the sender is bound, treat
            // them as unbound so commands that need binding don't proceed
            // against bot-owner credentials.
            _logger.LogError(ex, "Binding lookup for slash command {Command} failed; treating sender as unbound", commandName);
            existing = null;
        }

        return new SlashBindingLookup(true, true, subject, existing);
    }

    private static bool TryResolveLlmSelectionAction(
        CardActionSubmission? cardAction,
        InboundMessage inbound,
        out ResolvedLlmSelectionAction action)
    {
        action = ResolvedLlmSelectionAction.Empty;
        if (cardAction is null)
            return false;

        var payload = cardAction.LlmSelection;
        if (payload is not null && !string.IsNullOrWhiteSpace(payload.Action))
        {
            var resolvedAction = payload.Action.Trim();
            var value = resolvedAction switch
            {
                TextUserLlmOptionsRenderer.SelectServiceAction => !string.IsNullOrWhiteSpace(payload.ServiceId)
                    ? payload.ServiceId.Trim()
                    : ResolveCardActionValue(inbound, cardAction, TextUserLlmOptionsRenderer.ServiceIdArgument),
                TextUserLlmOptionsRenderer.ApplyPresetAction => !string.IsNullOrWhiteSpace(payload.PresetId)
                    ? payload.PresetId.Trim()
                    : ResolveCardActionValue(inbound, cardAction, TextUserLlmOptionsRenderer.PresetIdArgument),
                _ => string.Empty,
            };
            action = new ResolvedLlmSelectionAction(
                resolvedAction,
                value,
                payload.Page <= 0 ? 1 : payload.Page,
                ResolveDisplayMode(payload.DisplayMode));
            return true;
        }

        string actionName;
        if (!inbound.Extra.TryGetValue(TextUserLlmOptionsRenderer.LlmActionArgument, out var actionValue) ||
            string.IsNullOrWhiteSpace(actionValue))
        {
            // Deprecated inbound compatibility only. New producers must use LlmSelectionActionPayload.
            actionName = cardAction.ActionId switch
            {
                TextUserLlmOptionsRenderer.SelectServiceActionId => TextUserLlmOptionsRenderer.SelectServiceAction,
                TextUserLlmOptionsRenderer.ApplyPresetActionId => TextUserLlmOptionsRenderer.ApplyPresetAction,
                TextUserLlmOptionsRenderer.ListPageActionId => TextUserLlmOptionsRenderer.ListPageAction,
                TextUserLlmOptionsRenderer.LegacySelectServiceActionId => TextUserLlmOptionsRenderer.SelectServiceAction,
                TextUserLlmOptionsRenderer.LegacySelectModelActionId => TextUserLlmOptionsRenderer.LegacySelectModelAction,
                TextUserLlmOptionsRenderer.LegacyApplyPresetActionId => TextUserLlmOptionsRenderer.ApplyPresetAction,
                _ => string.Empty,
            };
        }
        else
        {
            actionName = actionValue;
        }

        actionName = actionName.Trim();
        var resolvedValue = actionName switch
        {
            TextUserLlmOptionsRenderer.SelectServiceAction =>
                ResolveCardActionValue(inbound, cardAction, TextUserLlmOptionsRenderer.ServiceIdArgument),
            TextUserLlmOptionsRenderer.ApplyPresetAction =>
                ResolveCardActionValue(inbound, cardAction, TextUserLlmOptionsRenderer.PresetIdArgument),
            TextUserLlmOptionsRenderer.ListPageAction =>
                ResolveCardActionValue(inbound, cardAction, TextUserLlmOptionsRenderer.PageArgument),
            _ => string.Empty,
        };

        action = new ResolvedLlmSelectionAction(
            actionName,
            resolvedValue,
            ResolvePage(resolvedValue),
            ResolveDisplayMode(ResolveCardActionValue(inbound, cardAction, "display_mode")));
        return !string.IsNullOrWhiteSpace(actionName);
    }

    private static string ResolveCardActionValue(
        InboundMessage inbound,
        CardActionSubmission cardAction,
        string argumentName)
    {
        if (inbound.Extra.TryGetValue(argumentName, out var argumentValue) &&
            !string.IsNullOrWhiteSpace(argumentValue))
        {
            return argumentValue.Trim();
        }

        if (cardAction.Arguments.TryGetValue(argumentName, out var cardArgumentValue) &&
            !string.IsNullOrWhiteSpace(cardArgumentValue))
        {
            return cardArgumentValue.Trim();
        }

        if (cardAction.FormFields.TryGetValue(argumentName, out var formFieldValue) &&
            !string.IsNullOrWhiteSpace(formFieldValue))
        {
            return formFieldValue.Trim();
        }

        return cardAction.SubmittedValue?.Trim() ?? string.Empty;
    }

    private static int ResolvePage(string? value) =>
        int.TryParse(value?.Trim(), out var page) && page > 0 ? page : 1;

    private static UserLlmSelectionDisplayMode ResolveDisplayMode(string? value) =>
        string.Equals(value?.Trim(), "route", StringComparison.OrdinalIgnoreCase)
            ? UserLlmSelectionDisplayMode.Route
            : UserLlmSelectionDisplayMode.Model;

    private readonly record struct ResolvedLlmSelectionAction(
        string Action,
        string Value,
        int Page,
        UserLlmSelectionDisplayMode DisplayMode)
    {
        public static readonly ResolvedLlmSelectionAction Empty = new(
            string.Empty,
            string.Empty,
            1,
            UserLlmSelectionDisplayMode.Model);
    }

    public async Task<ConversationTurnResult> RunLlmReplyAsync(
        LlmReplyReadyEvent reply,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if (reply.Activity is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "activity_required",
                "Deferred LLM reply is missing the source activity.");
        }

        var outboundIntent = reply.Outbound?.Clone() ?? new MessageContent();
        if (!HasContent(outboundIntent))
        {
            return ConversationTurnResult.TransientFailure(
                string.IsNullOrWhiteSpace(reply.ErrorCode) ? "empty_reply" : reply.ErrorCode,
                string.IsNullOrWhiteSpace(reply.ErrorSummary)
                    ? "Deferred LLM reply is empty."
                    : reply.ErrorSummary);
        }

        var inbound = ToInboundMessage(reply.Activity);
        // Direct path requires registration to actually send the reply; relay path only wants it
        // for the post-reply reaction clear (relay sends use the reply token, not registration).
        // So lookup is mandatory on the direct path and best-effort on the relay path — a
        // transient registration-store error on the relay path must not drop an otherwise valid
        // reply, only degrade the clear to a no-op for that turn.
        ChannelBotRegistrationEntry? registration;
        if (HasRelayDelivery(inbound))
        {
            try
            {
                registration = await ResolveRegistrationForReplyAsync(reply, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Registration lookup failed on relay reply path; reply will proceed but post-reply reaction clear will be skipped. correlation={CorrelationId}",
                    reply.CorrelationId);
                registration = null;
            }
        }
        else
        {
            registration = await ResolveRegistrationForReplyAsync(reply, ct);
            if (registration is null)
            {
                return ConversationTurnResult.PermanentFailure(
                    "registration_not_found",
                    "Channel registration not found.");
            }
        }

        var sentSeed = string.IsNullOrWhiteSpace(reply.CorrelationId)
            ? reply.Activity.Id
            : reply.CorrelationId;
        var result = await SendReplyAsync(
            outboundIntent,
            sentSeed,
            reply.Activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct);
        if (result.Success)
            _ = TryClearTypingReactionAsync(inbound, registration, ct);
        return result;
    }

    public Task<ConversationTurnResult> RunLlmReplyAsync(LlmReplyReadyEvent reply, CancellationToken ct) =>
        RunLlmReplyAsync(reply, ConversationTurnRuntimeContext.Empty, ct);

    public async Task<ConversationTurnResult> RunContinueAsync(
        ConversationContinueRequestedEvent command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Kind == PrincipalKind.OnBehalfOfUser)
        {
            return ConversationTurnResult.PermanentFailure(
                "unsupported_auth_context",
                "Legacy Lark outbound bridge does not support delegated proactive sends.");
        }

        var registration = await ResolveRegistrationAsync(command.Conversation?.Bot?.Value, ct);
        if (registration is null)
            return ConversationTurnResult.PermanentFailure("registration_not_found", "Channel registration not found.");

        var conversationId = ResolveRoutingConversationId(command.Conversation);
        if (string.IsNullOrWhiteSpace(conversationId))
            return ConversationTurnResult.PermanentFailure("conversation_not_found", "Conversation routing target is missing.");

        var inbound = new InboundMessage
        {
            Platform = registration.Platform,
            ConversationId = conversationId,
            SenderId = command.OnBehalfOfUserId ?? string.Empty,
            SenderName = string.Empty,
            Text = command.Payload?.Text ?? string.Empty,
            MessageId = command.CommandId,
            ChatType = ResolveChatType(command.Conversation),
        };

        return await SendReplyAsync(
            command.Payload?.Clone() ?? new MessageContent(),
            command.CommandId,
            command.Conversation,
            inbound,
            registration,
            ConversationTurnRuntimeContext.Empty,
            ct);
    }

    public async Task OnReplyDeliveredAsync(ChatActivity activity, CancellationToken ct)
    {
        // Streaming-completion path in ConversationGAgent calls this hook because it finalizes
        // the reply without going through RunLlmReplyAsync (which is where the non-streaming clear
        // lives). For non-Lark platforms or activities missing the platform message id, the clear
        // helper short-circuits in ShouldClearTypingReaction.
        if (activity is null)
            return;

        var registration = await ResolveRegistrationAsync(activity, ct);
        if (registration is null)
            return;

        var inbound = ToInboundMessage(activity);
        await TryClearTypingReactionAsync(inbound, registration, ct);
    }

    public async Task<ConversationStreamChunkResult> RunStreamChunkAsync(
        LlmReplyStreamChunkEvent chunk,
        string? currentPlatformMessageId,
        NyxRelayTextOperationKind operation,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (chunk.Activity is null)
        {
            return ConversationStreamChunkResult.Failed(
                "activity_required",
                "Stream chunk event is missing the source activity.");
        }

        var inbound = ToInboundMessage(chunk.Activity);
        if (!HasRelayDelivery(inbound))
        {
            return ConversationStreamChunkResult.Failed(
                "invalid_delivery",
                "Stream chunk requires a relay outbound delivery context.");
        }

        var relayDelivery = inbound.OutboundDelivery!.Clone();
        var relayToken = ResolveRelayReplyToken(relayDelivery, runtimeContext);
        if (relayToken is null)
        {
            return ConversationStreamChunkResult.Failed(
                "reply_token_missing_or_expired",
                "Nyx relay reply token is missing or expired for this streaming chunk.");
        }

        var conversation = chunk.Activity.Conversation;
        var platform = ResolveRelayPlatform(inbound, conversation);
        var content = new MessageContent
        {
            Text = FormatReplyTextForPlatform(platform, NormalizeReplyText(chunk.AccumulatedText)),
        };
        var segments = operation == NyxRelayTextOperationKind.Final &&
                       !string.IsNullOrWhiteSpace(currentPlatformMessageId)
            ? ChannelTextMessageSegmenter.Segment(content.Text)
            : null;
        var updateContent = segments is { Count: > 1 }
            ? new MessageContent { Text = segments[0] }
            : content;

        EmitResult emit;
        if (string.IsNullOrWhiteSpace(currentPlatformMessageId))
        {
            emit = await _relayOutboundPort.SendAsync(
                platform,
                conversation?.Clone() ?? new ConversationReference(),
                content,
                relayDelivery,
                relayToken,
                ct);
        }
        else
        {
            emit = await _relayOutboundPort.UpdateAsync(
                platform,
                conversation?.Clone() ?? new ConversationReference(),
                updateContent,
                relayDelivery,
                currentPlatformMessageId,
                relayToken,
                ct);
        }

        if (!emit.Success)
        {
            var editUnsupported = string.Equals(
                emit.ErrorCode,
                "relay_reply_edit_unsupported",
                StringComparison.Ordinal);
            return ConversationStreamChunkResult.Failed(
                string.IsNullOrWhiteSpace(emit.ErrorCode) ? "stream_chunk_rejected" : emit.ErrorCode,
                emit.ErrorMessage ?? "Relay stream chunk rejected.",
                editUnsupported,
                emit.FailureKind,
                emit.RetryAfterTimeSpan,
                emit.HttpStatus,
                emit.RawErrorKey,
                emit.RawErrorCode);
        }

        var resolvedPlatformMessageId = string.IsNullOrWhiteSpace(emit.PlatformMessageId)
            ? currentPlatformMessageId
            : emit.PlatformMessageId;
        if (segments is { Count: > 1 })
        {
            var tailResult = await SendTailTextSegmentsAsync(
                    chunk,
                    inbound,
                    platform,
                    runtimeContext,
                    segments.Skip(1),
                    ct)
                .ConfigureAwait(false);
            if (!tailResult.Success)
                return tailResult;
        }

        return ConversationStreamChunkResult.Succeeded(resolvedPlatformMessageId);
    }

    private async Task<ConversationStreamChunkResult> SendTailTextSegmentsAsync(
        LlmReplyStreamChunkEvent chunk,
        InboundMessage inbound,
        string platform,
        ConversationTurnRuntimeContext runtimeContext,
        IEnumerable<string> tailSegments,
        CancellationToken ct)
    {
        var nyxProxyCredential = ResolveUserAccessToken(chunk.Activity!, runtimeContext);
        var sender = _relayTailTextSender;
        if (sender is null)
            return ConversationStreamChunkResult.Failed("relay_tail_segment_sender_missing", "Relay tail text sender is not registered.");

        var result = await sender.SendTailSegmentsAsync(
                new ChannelRelayTailTextSendRequest(
                    platform,
                    inbound.ChatType ?? string.Empty,
                    inbound.ConversationId ?? string.Empty,
                    inbound.SenderId ?? string.Empty,
                    inbound.TransportExtras,
                    nyxProxyCredential ?? string.Empty,
                    tailSegments.ToArray(),
                    chunk.CorrelationId),
                ct)
            .ConfigureAwait(false);

        return result.Succeeded
            ? ConversationStreamChunkResult.Succeeded(null)
            : ConversationStreamChunkResult.Failed(
                result.ErrorCode,
                result.Detail,
                failureKind: result.FailureKind,
                rawErrorCode: result.RawErrorCode);
    }

    private async Task<ConversationTurnResult?> TryHandleAgentBuilderAsync(
        ChatActivity activity,
        ChannelInboundEvent inboundEvent,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        ResolvedSenderBinding? senderBinding,
        Task typingReactionTask,
        CancellationToken ct)
    {
        var decision = await AgentBuilderCardFlow.TryResolveAsync(
            inboundEvent,
            _userConfigQueryPort,
            ct);
        if (decision is null)
        {
            // No slash-command/card flow matched. AgentBuilderCardFlow explicitly leaves
            // non-slash text and unknown slash shortcuts for the LLM fallback path.
            return null;
        }

        var replyContent = decision.ReplyContent ?? new MessageContent { Text = decision.ReplyPayload };
        if (decision.RequiresToolExecution)
        {
            var channelContext = await BuildAgentBuilderChannelContextAsync(
                    activity,
                    inboundEvent,
                    runtimeContext,
                    ct);
            var executionContext = BuildAgentBuilderToolContext(
                    inboundEvent,
                    activity,
                    registration,
                    ResolveUserAccessToken(activity, runtimeContext),
                    senderBinding,
                    channelContext.Metadata,
                    channelContext.IdentityHints)
                .WithCallId($"{inboundEvent.MessageId}:agent-builder") with
            {
                ExecutionOwner = AgentToolExecutionOwners.ChannelRegistration(registration.Id),
            };
            var tool = ActivatorUtilities.CreateInstance<AgentBuilderTool>(_toolServiceProvider);
            var outcome = await _toolExecutionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    tool,
                    decision.ToolArgumentsJson!,
                    executionContext,
                    AgentToolApprovalContinuationMode.None,
                    null),
                ct).ConfigureAwait(false);
            replyContent = AgentBuilderCardFlow.FormatToolResult(decision, outcome.ResultJson);
        }

        var inbound = ToInboundMessage(activity);
        var result = await SendReplyAsync(
            replyContent,
            activity.Id,
            activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct);
        if (result.Success)
            _ = AwaitTypingReactionThenClearAsync(typingReactionTask, inbound, registration, ct);
        return result.Success
            ? ConversationTurnResult.Sent(
                sentActivityId: $"direct-reply:{activity.Id}",
                outbound: replyContent.Clone(),
                authPrincipal: "bot",
                outboundDelivery: result.OutboundDelivery?.Clone())
            : result;
    }

    private async Task<ConversationTurnResult> SendReplyAsync(
        string replyText,
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct) =>
        await SendReplyAsync(
            new MessageContent { Text = replyText },
            activity.Id,
            activity.Conversation,
            inbound,
            registration,
            runtimeContext,
            ct);

    private async Task<ConversationTurnResult> SendReplyAsync(
        MessageContent outboundIntent,
        string sentActivitySeed,
        ConversationReference? conversation,
        InboundMessage inbound,
        ChannelBotRegistrationEntry? registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outboundIntent);

        if (HasRelayDelivery(inbound))
        {
            var relayDelivery = inbound.OutboundDelivery!.Clone();
            var relayToken = ResolveRelayReplyToken(relayDelivery, runtimeContext);
            if (relayToken is null)
            {
                return ConversationTurnResult.PermanentFailure(
                    "reply_token_missing_or_expired",
                    "Nyx relay reply token is missing or expired for this conversation turn.");
            }

            if (await TrySendInteractiveRelayReplyAsync(
                    outboundIntent,
                    sentActivitySeed,
                    conversation,
                    inbound,
                    relayDelivery,
                    relayToken,
                    ct) is { } interactiveResult)
            {
                return interactiveResult;
            }

            var emit = await _relayOutboundPort.SendAsync(
                ResolveRelayPlatform(inbound, conversation),
                conversation?.Clone() ?? new ConversationReference(),
                outboundIntent,
                relayDelivery,
                relayToken,
                ct);
            return emit.Success
                ? BuildRelaySentResult(
                    emit.SentActivityId,
                    sentActivitySeed,
                    outboundIntent,
                    relayDelivery)
                : ToRelayFailure(emit);
        }

        if (registration is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "registration_not_found",
                "Channel registration not found.");
        }

        var adapter = _platformAdapters.FirstOrDefault(platformAdapter =>
            string.Equals(platformAdapter.Platform, registration.Platform, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            return ConversationTurnResult.PermanentFailure(
                "adapter_not_found",
                $"No platform adapter registered for '{registration.Platform}'.");
        }

        var replyText = FormatReplyTextForPlatform(
            registration.Platform,
            NormalizeReplyText(
                string.IsNullOrWhiteSpace(outboundIntent.Text) && HasInteractiveContent(outboundIntent)
                    ? NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(outboundIntent)
                    : outboundIntent.Text));
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return ConversationTurnResult.TransientFailure(
                "empty_reply",
                "Deferred LLM reply is empty.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var replyService = _replyService;
        var delivery = replyService is not null
            ? await replyService.DeliverAsync(adapter, replyText, inbound, registration, cts.Token)
            : await adapter.SendReplyAsync(replyText, inbound, registration, _nyxClient, cts.Token);
        if (!delivery.Succeeded)
        {
            _logger.LogWarning(
                "Channel conversation reply rejected: registration={RegistrationId}, detail={Detail}, kind={Kind}",
                registration.Id,
                delivery.Detail,
                delivery.FailureKind);
            return delivery.FailureKind == PlatformReplyFailureKind.Permanent
                ? ConversationTurnResult.PermanentFailure("reply_rejected", delivery.Detail ?? "reply rejected")
                : ConversationTurnResult.TransientFailure("reply_rejected", delivery.Detail ?? "reply rejected");
        }

        return ConversationTurnResult.Sent(
            sentActivityId: $"direct-reply:{sentActivitySeed}",
            outbound: new MessageContent { Text = replyText },
            authPrincipal: "bot",
            outboundDelivery: inbound.OutboundDelivery?.Clone());
    }

    private async Task<ConversationTurnResult?> TrySendInteractiveRelayReplyAsync(
        MessageContent outboundIntent,
        string sentActivitySeed,
        ConversationReference? conversation,
        InboundMessage inbound,
        OutboundDeliveryContext relayDelivery,
        string relayToken,
        CancellationToken ct)
    {
        var relayChannel = ResolveRelayChannel(inbound, conversation);
        var hasJsonTable = IsLarkChannel(relayChannel) &&
                           LarkJsonTableFormatter.ContainsConvertibleJson(outboundIntent.Text);
        if (!HasInteractiveContent(outboundIntent) && !hasJsonTable)
            return null;

        var fallbackText = hasJsonTable
            ? NormalizeReplyText(LarkJsonTableFormatter.FormatAsKeyValueText(outboundIntent.Text))
            : NormalizeReplyText(NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(outboundIntent));
        if (_interactiveReplyDispatcher is null)
        {
            _logger.LogWarning(
                "Interactive relay reply requested without dispatcher; degrading to text. messageId={MessageId}",
                relayDelivery.ReplyMessageId);
            return await SendRelayTextFallbackAsync(
                fallbackText,
                sentActivitySeed,
                conversation,
                inbound,
                relayDelivery,
                relayToken,
                ct);
        }

        var dispatch = await _interactiveReplyDispatcher.DispatchAsync(
            relayChannel,
            relayDelivery.ReplyMessageId,
            relayToken,
            outboundIntent,
            new ComposeContext
            {
                Conversation = conversation?.Clone() ?? new ConversationReference(),
            },
            ct);
        if (dispatch.Succeeded)
        {
            var delivered = dispatch.FellBackToText
                ? new MessageContent { Text = fallbackText }
                : outboundIntent.Clone();
            return BuildRelaySentResult(
                dispatch.MessageId,
                sentActivitySeed,
                delivered,
                relayDelivery);
        }

        // The dispatcher has already consumed the relay reply token via NyxID's
        // `channel-relay/reply` endpoint — even when the upstream returns 5xx, NyxID's
        // single-use semantics mark the token as used before the failure surfaces. A second
        // call with the same token (the previous "degrade to text" retry) lands as
        // `401 Reply token already used`, which then escapes as a hard relay failure and
        // queues an inbound turn retry that re-consumes the (already gone) token forever
        // — observed in production after PR #409 introduced interactive cards: NyxID
        // returned 502 for the card payload, the legacy fallback re-sent as text and got
        // 401, and the bot looked silent on every subsequent DM.
        //
        // Use the distinct `relay_reply_token_consumed` error code so `ToRelayFailure` maps
        // it to `PermanentFailure` (vs. transient). Without this, `ConversationGAgent
        // .HandleInboundTurnTransientFailureAsync` would queue an `InboundTurnRetryScheduled
        // Event` and re-run the same inbound turn with the same already-consumed token —
        // shifting the 401 cascade from in-turn replay (fixed) to grain-level replay (still
        // broken). The token is single-use, so we get exactly one attempt per inbound; if
        // that fails, the only correct recovery is to NOT replay it.
        _logger.LogWarning(
            "Interactive relay reply rejected; reply token consumed, not retrying. messageId={MessageId}, detail={Detail}",
            relayDelivery.ReplyMessageId,
            dispatch.Detail);
        return ToRelayFailure(EmitResult.Failed(
            "relay_reply_token_consumed",
            string.IsNullOrWhiteSpace(dispatch.Detail)
                ? "Interactive relay reply rejected; reply token consumed."
                : dispatch.Detail));
    }

    private async Task<ConversationTurnResult> SendRelayTextFallbackAsync(
        string? fallbackText,
        string sentActivitySeed,
        ConversationReference? conversation,
        InboundMessage inbound,
        OutboundDeliveryContext relayDelivery,
        string relayToken,
        CancellationToken ct)
    {
        var outbound = new MessageContent { Text = NormalizeReplyText(fallbackText) };
        var emit = await _relayOutboundPort.SendAsync(
            ResolveRelayPlatform(inbound, conversation),
            conversation?.Clone() ?? new ConversationReference(),
            outbound,
            relayDelivery,
            relayToken,
            ct);
        return emit.Success
            ? BuildRelaySentResult(
                emit.SentActivityId,
                sentActivitySeed,
                outbound,
                relayDelivery)
            : ToRelayFailure(emit);
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationAsync(string? registrationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registrationId))
            return null;

        return await _registrationQueryPort.GetAsync(registrationId, ct);
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationAsync(ChatActivity activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var nyxAgentApiKeyId = NormalizeOptional(activity.TransportExtras?.NyxAgentApiKeyId);
        var canonicalScopeId = NormalizeOptional(activity.TransportExtras?.NyxRegistrationScopeId);
        if (!string.IsNullOrWhiteSpace(nyxAgentApiKeyId))
        {
            if (_registrationQueryByNyxIdentityPort is null)
                return null;

            var registrations = await _registrationQueryByNyxIdentityPort.ListByNyxAgentApiKeyIdAsync(
                nyxAgentApiKeyId,
                ct);
            var byNyxIdentity = ResolveRegistrationByNyxIdentityCandidates(registrations, canonicalScopeId);

            if (byNyxIdentity is not null)
                return byNyxIdentity;

            if (registrations.Count > 0)
                return null;
        }

        var byBotId = await ResolveRegistrationAsync(activity.Bot?.Value, ct);
        return byBotId is not null && IsBotIdFallbackRegistrationAllowed(byBotId, canonicalScopeId, nyxAgentApiKeyId)
            ? byBotId
            : null;
    }

    private static ChannelBotRegistrationEntry? ResolveRegistrationByNyxIdentityCandidates(
        IReadOnlyList<ChannelBotRegistrationEntry> registrations,
        string? canonicalScopeId)
    {
        if (registrations.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(canonicalScopeId))
        {
            return registrations.FirstOrDefault(entry =>
                string.Equals(NormalizeOptional(entry.ScopeId), canonicalScopeId, StringComparison.Ordinal));
        }

        var distinctScopeIds = registrations
            .Select(entry => NormalizeOptional(entry.ScopeId))
            .Where(static scopeId => scopeId is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinctScopeIds.Length != 1)
            return null;

        var resolvedScopeId = distinctScopeIds[0];
        return registrations.FirstOrDefault(entry =>
            string.Equals(NormalizeOptional(entry.ScopeId), resolvedScopeId, StringComparison.Ordinal));
    }

    private static bool IsBotIdFallbackRegistrationAllowed(
        ChannelBotRegistrationEntry registration,
        string? canonicalScopeId,
        string? nyxAgentApiKeyId)
    {
        if (string.IsNullOrWhiteSpace(canonicalScopeId))
            return true;

        if (!string.Equals(NormalizeOptional(registration.ScopeId), canonicalScopeId, StringComparison.Ordinal))
            return false;

        var registrationApiKeyId = NormalizeOptional(registration.NyxAgentApiKeyId);
        return registrationApiKeyId is null ||
               string.Equals(registrationApiKeyId, nyxAgentApiKeyId, StringComparison.Ordinal);
    }

    private async Task<ChannelBotRegistrationEntry?> ResolveRegistrationForReplyAsync(
        LlmReplyReadyEvent reply,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(reply.RegistrationId))
            return await ResolveRegistrationAsync(reply.RegistrationId, ct);

        if (reply.Activity is not null)
            return await ResolveRegistrationAsync(reply.Activity, ct);

        return null;
    }

    private async Task<ConversationTurnResult?> TryHandleWorkflowResumeAsync(InboundMessage inbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        var routed = ChannelCardActionRouting.TryBuildWorkflowResumeCommand(inbound, out var resumeCommand);
        if (!routed)
            routed = ChannelWorkflowTextRouting.TryBuildWorkflowResumeCommand(inbound, out resumeCommand);

        if (!routed ||
            resumeCommand is null)
        {
            return null;
        }

        var resumeService = _workflowResumeService;
        if (resumeService is null)
        {
            _logger.LogError(
                "Workflow resume service unavailable for registration callback: conversation={ConversationId}",
                inbound.ConversationId);
            return ConversationTurnResult.TransientFailure(
                "workflow_resume_service_unavailable",
                "Workflow resume service unavailable.");
        }

        var dispatch = await resumeService.DispatchAsync(resumeCommand, ct);
        if (!dispatch.Succeeded || dispatch.Receipt is null)
        {
            var error = dispatch.Error;
            if (error is null)
            {
                return ConversationTurnResult.TransientFailure(
                    "workflow_resume_dispatch_failed",
                    "Workflow control dispatch failed.");
            }

            return error.Code switch
            {
                WorkflowRunControlStartErrorCode.InvalidActorId =>
                    ConversationTurnResult.PermanentFailure("invalid_actor_id", "actorId is required."),
                WorkflowRunControlStartErrorCode.InvalidRunId =>
                    ConversationTurnResult.PermanentFailure("invalid_run_id", "runId is required."),
                WorkflowRunControlStartErrorCode.InvalidStepId =>
                    ConversationTurnResult.PermanentFailure("invalid_step_id", "stepId is required."),
                WorkflowRunControlStartErrorCode.ActorNotFound =>
                    ConversationTurnResult.PermanentFailure("actor_not_found", $"Actor '{error.ActorId}' not found."),
                WorkflowRunControlStartErrorCode.ActorNotWorkflowRun =>
                    ConversationTurnResult.PermanentFailure(
                        "actor_not_workflow_run",
                        $"Actor '{error.ActorId}' is not a workflow run actor."),
                WorkflowRunControlStartErrorCode.RunBindingMissing =>
                    ConversationTurnResult.PermanentFailure(
                        "run_binding_missing",
                        $"Actor '{error.ActorId}' does not have a bound run id."),
                WorkflowRunControlStartErrorCode.RunBindingMismatch =>
                    ConversationTurnResult.PermanentFailure(
                        "run_binding_mismatch",
                        $"Actor '{error.ActorId}' is bound to run '{error.BoundRunId}', not '{error.RequestedRunId}'."),
                _ => ConversationTurnResult.TransientFailure(
                    "workflow_resume_dispatch_failed",
                    "Workflow control dispatch failed."),
            };
        }

        return ConversationTurnResult.Sent(
            sentActivityId: $"workflow-resume:{dispatch.Receipt.CommandId}",
            outbound: new MessageContent(),
            authPrincipal: "bot");
    }

    private async Task<ConversationTurnResult?> TryHandleWorkflowDraftRunAsync(
        ChatActivity activity,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        ChannelInboundEvent inboundEvent,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        var admission = _workflowDraftRunAdmission;
        if (admission is null)
            return null;

        ExternalSubjectRef? senderSubject = null;
        if (TryResolveExternalSubject(inbound, registration, out var resolvedSenderSubject))
            senderSubject = resolvedSenderSubject;

        var result = await admission.TryAdmitAsync(
                activity,
                registration,
                inboundEvent,
                runtimeContext,
                senderSubject,
                ct)
            .ConfigureAwait(false);
        if (!result.Matched)
            return null;

        if (result.Request is not null)
            return ConversationTurnResult.WorkflowDraftRunRequested(result.Request);

        var rejection = result.Rejection ?? new MessageContent { Text = "暂不能运行 workflow,请稍后重试。" };
        return await SendReplyAsync(
                rejection,
                string.IsNullOrWhiteSpace(activity.Id) ? Guid.NewGuid().ToString("N") : activity.Id,
                activity.Conversation,
                inbound,
                registration,
                runtimeContext,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<ReplyChannelContext> BuildReplyChannelContextAsync(
        ChannelInboundEvent inboundEvent,
        ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChannelMetadataKeys.Platform] = inboundEvent.Platform,
            [ChannelMetadataKeys.SenderId] = inboundEvent.SenderId,
            [ChannelMetadataKeys.SenderName] = inboundEvent.SenderName,
            [ChannelMetadataKeys.ConversationId] = inboundEvent.ConversationId,
            [ChannelMetadataKeys.MessageId] = inboundEvent.MessageId,
            [ChannelMetadataKeys.ChatType] = inboundEvent.ChatType,
        };
        var identityHints = new List<AgentToolChannelIdentityHint>();

        // Inbound channel-bot's NyxID provider slug. Scheduled workflow creation captures this
        // as the failure-notification provider so a failed outbound delivery
        // (e.g. cross-tenant Lark 99992364) can still notify the user via the bot they just
        // successfully messaged. See issue #423 §C and ChannelMetadataKeys.InboundChannelBotProxySlug.
        if (!string.IsNullOrWhiteSpace(inboundEvent.NyxProviderSlug))
        {
            metadata[ChannelMetadataKeys.InboundChannelBotProxySlug] = inboundEvent.NyxProviderSlug;
            metadata[ChannelMetadataKeys.OutboundProviderSlug] = inboundEvent.NyxProviderSlug;
            // The inbound bot is also the default OUTBOUND delivery provider for a chat-triggered
            // scheduled task: the scheduled run replies via the same Lark bot that received the
            // message, so scheduled_agent_creator can resolve a provider without manual Studio/Web
            // config. A distinct outbound provider remains expressible explicitly via
            // agent_delivery_targets.
        }

        var platformMessageId = NormalizeOptional(activity?.TransportExtras?.NyxPlatformMessageId);
        if (!string.IsNullOrWhiteSpace(platformMessageId))
            metadata[ChannelMetadataKeys.PlatformMessageId] = platformMessageId;

        // Lark cross-app outbound delivery: agent-builder consumers prefer the tenant-stable
        // union_id / chat_id captured at ingress over the relay-app-scoped open_id, so a
        // mismatch between the relay-side Lark app and the customer's outbound Lark app does
        // not surface as `code:99992361 open_id cross app` rejections at send time.
        var larkUnionId = NormalizeOptional(activity?.TransportExtras?.NyxLarkUnionId);
        if (!string.IsNullOrWhiteSpace(larkUnionId))
        {
            metadata[ChannelMetadataKeys.LarkUnionId] = larkUnionId;
            AddIdentityHint(identityHints, "sender", "global", larkUnionId);
        }

        var larkChatId = NormalizeOptional(activity?.TransportExtras?.NyxLarkChatId);
        if (!string.IsNullOrWhiteSpace(larkChatId))
        {
            metadata[ChannelMetadataKeys.LarkChatId] = larkChatId;
            AddIdentityHint(identityHints, "conversation", "platform", larkChatId);
        }

        var deliveryAddressId = NormalizeOptional(activity?.TransportExtras?.DeliveryAddressId);
        if (!string.IsNullOrWhiteSpace(deliveryAddressId))
            metadata[ChannelMetadataKeys.DeliveryAddressId] = deliveryAddressId;

        var deliveryAddressType = NormalizeOptional(activity?.TransportExtras?.DeliveryAddressType);
        if (!string.IsNullOrWhiteSpace(deliveryAddressType))
            metadata[ChannelMetadataKeys.DeliveryAddressType] = deliveryAddressType;

        var deliveryFallbackAddressId = NormalizeOptional(activity?.TransportExtras?.DeliveryFallbackAddressId);
        if (!string.IsNullOrWhiteSpace(deliveryFallbackAddressId))
            metadata[ChannelMetadataKeys.DeliveryFallbackAddressId] = deliveryFallbackAddressId;

        var deliveryFallbackAddressType = NormalizeOptional(activity?.TransportExtras?.DeliveryFallbackAddressType);
        if (!string.IsNullOrWhiteSpace(deliveryFallbackAddressType))
            metadata[ChannelMetadataKeys.DeliveryFallbackAddressType] = deliveryFallbackAddressType;

        var larkOperatorUserId = NormalizeOptional(activity?.TransportExtras?.NyxLarkOperatorUserId);
        if (!string.IsNullOrWhiteSpace(larkOperatorUserId))
        {
            metadata[ChannelMetadataKeys.LarkOperatorUserId] = larkOperatorUserId;
            AddIdentityHint(identityHints, "operator", "account", larkOperatorUserId);
        }

        var larkOperatorOpenId = NormalizeOptional(activity?.TransportExtras?.NyxLarkOperatorOpenId);
        if (!string.IsNullOrWhiteSpace(larkOperatorOpenId))
        {
            metadata[ChannelMetadataKeys.LarkOperatorOpenId] = larkOperatorOpenId;
            AddIdentityHint(identityHints, "operator", "platform", larkOperatorOpenId);
        }

        var larkOperatorUnionId = NormalizeOptional(activity?.TransportExtras?.NyxLarkOperatorUnionId);
        if (!string.IsNullOrWhiteSpace(larkOperatorUnionId))
        {
            metadata[ChannelMetadataKeys.LarkOperatorUnionId] = larkOperatorUnionId;
            AddIdentityHint(identityHints, "operator", "global", larkOperatorUnionId);
        }

        if (await TryResolveLarkSubjectContactIdsAsync(inboundEvent, activity, runtimeContext, larkUnionId, ct)
                .ConfigureAwait(false) is { } subjectContactIds)
        {
            if (!string.IsNullOrWhiteSpace(subjectContactIds.UserId))
            {
                metadata[ChannelMetadataKeys.LarkSubjectUserId] = subjectContactIds.UserId;
                AddIdentityHint(identityHints, "subject", "account", subjectContactIds.UserId);
            }

            if (!string.IsNullOrWhiteSpace(subjectContactIds.EmployeeId))
            {
                metadata[ChannelMetadataKeys.LarkSubjectEmployeeId] = subjectContactIds.EmployeeId;
                AddIdentityHint(identityHints, "subject", "directory", subjectContactIds.EmployeeId);
            }
        }

        // Surface resolved @-mentions (canonical id + name) so the agent can target a third party by a
        // real id instead of the literal "@_user_N" text placeholder. Placeholder numbering follows the
        // mention order, so the list order is preserved. The bot's own mention may be included; the
        // prompt instructs the agent to pick the non-bot entry.
        if (activity?.Mentions is { Count: > 0 } mentions)
        {
            var formattedMentions = string.Join(
                "; ",
                mentions
                    .Where(mention => !string.IsNullOrWhiteSpace(mention.CanonicalId))
                    .Select(mention =>
                        $"{(string.IsNullOrWhiteSpace(mention.DisplayName) ? "?" : mention.DisplayName)} <{mention.CanonicalId}>"));
            if (!string.IsNullOrWhiteSpace(formattedMentions))
                metadata[ChannelMetadataKeys.Mentions] = formattedMentions;
        }

        return new ReplyChannelContext(metadata, identityHints);
    }

    private static void AddIdentityHint(
        ICollection<AgentToolChannelIdentityHint> identityHints,
        string subject,
        string kind,
        string value) =>
        identityHints.Add(new AgentToolChannelIdentityHint(subject, kind, value));

    private async Task<LarkSubjectContactIds?> TryResolveLarkSubjectContactIdsAsync(
        ChannelInboundEvent inboundEvent,
        ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext,
        string? larkUnionId,
        CancellationToken ct)
    {
        if (activity?.Type != ActivityType.Message)
            return null;

        if (!IsLarkPlatform(inboundEvent.Platform))
            return null;

        var accessToken = activity is null
            ? NormalizeOptional(runtimeContext.NyxUserAccessToken)
            : ResolveUserAccessToken(activity, runtimeContext);
        var providerSlug = NormalizeOptional(inboundEvent.NyxProviderSlug);
        var scopeId = NormalizeOptional(inboundEvent.RegistrationScopeId);
        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(providerSlug) ||
            string.IsNullOrWhiteSpace(scopeId))
        {
            return null;
        }

        var lookupId = NormalizeOptional(larkUnionId);
        var userIdType = "union_id";
        if (string.IsNullOrWhiteSpace(lookupId))
        {
            lookupId = NormalizeOptional(inboundEvent.SenderId);
            userIdType = "open_id";
        }

        if (string.IsNullOrWhiteSpace(lookupId))
            return null;

        try
        {
            var response = await _nyxClient.ProxyRequestAsync(
                    accessToken!,
                    providerSlug!,
                    $"/open-apis/contact/v3/users/{Uri.EscapeDataString(lookupId!)}?user_id_type={userIdType}",
                    "GET",
                    body: null,
                    extraHeaders: null,
                    ct)
                .ConfigureAwait(false);

            if (ClassifyRelayProxyResponse(response).IsError)
                return null;

            return TryParseLarkSubjectContactIds(response, out var contactIds) ? contactIds : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lark subject contact lookup failed open: provider={ProviderSlug}, userIdType={UserIdType}",
                providerSlug,
                userIdType);
            return null;
        }
    }

    private static bool TryParseLarkSubjectContactIds(string? response, out LarkSubjectContactIds contactIds)
    {
        contactIds = new LarkSubjectContactIds(null, null);
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("user", out var user) ||
                user.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var userId = TryReadString(user, "user_id");
            var employeeId = TryReadString(user, "employee_id");
            if (string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(employeeId))
                return false;

            contactIds = new LarkSubjectContactIds(userId, employeeId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsLarkPlatform(string? platform) =>
        string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadString(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return NormalizeOptional(property.GetString());
    }

    private static AgentToolExecutionContext BuildAgentBuilderToolContext(
        ChannelInboundEvent inboundEvent,
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        string? userAccessToken,
        ResolvedSenderBinding? senderBinding,
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyList<AgentToolChannelIdentityHint> identityHints)
    {
        var token = NormalizeOptional(userAccessToken);
        return AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(inboundEvent.MessageId, null),
            Credentials = new AgentToolCredentials(token, token, null),
            Caller = new AgentToolCallerContext(
                inboundEvent.RegistrationScopeId,
                inboundEvent.RegistrationScopeId,
                inboundEvent.MessageId,
                senderBinding?.OwnerScopeId),
            Channel = new AgentToolChannelContext(
                inboundEvent.Platform,
                inboundEvent.SenderId,
                inboundEvent.RegistrationScopeId,
                inboundEvent.MessageId,
                NormalizeOptional(activity.TransportExtras?.NyxPlatformMessageId),
                null,
                BuildWorkflowResultDeliveryCredential(registration),
                NormalizeOptional(registration.Id),
                identityHints),
            ExternalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(metadata),
        };
    }

    /// <summary>
    /// Composes the typed workflow result delivery handle from the bot registration read model:
    /// the vault <c>SecretReference</c> persisted at provisioning plus the NyxID agent api-key id
    /// it authorizes (the vault subject). Null when the registration carries no vault handle —
    /// workflow background delivery then fails closed before any run is dispatched.
    /// </summary>
    private static ChannelWorkflowResultDeliveryCredential? BuildWorkflowResultDeliveryCredential(
        ChannelBotRegistrationEntry registration)
    {
        var secretReference = registration.WorkflowResultDeliveryCredential;
        if (string.IsNullOrWhiteSpace(secretReference?.Ref) ||
            string.IsNullOrWhiteSpace(registration.NyxAgentApiKeyId))
            return null;

        return new ChannelWorkflowResultDeliveryCredential
        {
            SecretReference = secretReference.Clone(),
            SubjectId = registration.NyxAgentApiKeyId.Trim(),
        };
    }

    private async Task<ReplyChannelContext> BuildAgentBuilderChannelContextAsync(
        ChatActivity activity,
        ChannelInboundEvent inboundEvent,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        var replyChannelContext = await BuildReplyChannelContextAsync(inboundEvent, activity, runtimeContext, ct);
        var metadata = new Dictionary<string, string>(replyChannelContext.Metadata, StringComparer.Ordinal)
        {
            [ChannelMetadataKeys.ChatType] = ResolveConversationChatType(activity.Conversation),
        };
        return new ReplyChannelContext(metadata, replyChannelContext.IdentityHints);
    }

    internal static InboundMessage ToInboundMessage(ChatActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        var cardAction = activity.Type == ActivityType.CardAction
            ? activity.Content?.CardAction
            : null;
        if (cardAction is not null)
        {
            if (cardAction.Arguments.TryGetValue("agent_builder_action", out var builderAction) &&
                !string.IsNullOrWhiteSpace(builderAction))
            {
                extra["agent_builder_action"] = builderAction;
            }
            else if (!string.IsNullOrWhiteSpace(cardAction.ActionId))
            {
                extra["agent_builder_action"] = cardAction.ActionId;
            }

            foreach (var pair in cardAction.Arguments)
                extra[pair.Key] = pair.Value;
            foreach (var pair in cardAction.FormFields)
                extra[pair.Key] = pair.Value;
            if (!string.IsNullOrWhiteSpace(cardAction.SourceMessageId))
                extra["event_id"] = cardAction.SourceMessageId;
        }

        return new InboundMessage
        {
            Platform = activity.ChannelId?.Value ?? string.Empty,
            ConversationId = ResolveRoutingConversationId(activity.Conversation),
            SenderId = activity.From?.CanonicalId ?? string.Empty,
            SenderName = activity.From?.DisplayName ?? string.Empty,
            Text = activity.Content?.Text ?? string.Empty,
            MessageId = activity.Id,
            ChatType = ResolveChatType(activity.Conversation, activity.Type),
            OutboundDelivery = activity.OutboundDelivery?.Clone(),
            TransportExtras = activity.TransportExtras?.Clone(),
            CardAction = cardAction?.Clone(),
            Extra = extra,
        };
    }

    private static bool TryBuildGenericFormSubmitLlmText(CardActionSubmission? cardAction, out string text)
    {
        text = string.Empty;
        if (cardAction is null ||
            cardAction.ActionKind != ActionElementKind.FormSubmit ||
            cardAction.FormFields.Count == 0)
        {
            return false;
        }

        var lines = cardAction.FormFields
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key}: {pair.Value}")
            .ToArray();
        if (lines.Length == 0)
            return false;

        text = string.Join("\n", lines);
        return true;
    }

    private static bool TryBuildGenericButtonClickLlmText(CardActionSubmission? cardAction, out string text)
    {
        text = string.Empty;
        if (cardAction is null ||
            cardAction.ActionKind != ActionElementKind.Button ||
            string.IsNullOrWhiteSpace(cardAction.ActionId))
        {
            return false;
        }

        // Typed payloads belong to their dedicated routers; reaching this point with one
        // attached means that router declined, and promoting it to a generic LLM turn
        // would bypass the typed contract.
        if (cardAction.WorkflowResume is not null ||
            cardAction.LlmSelection is not null ||
            cardAction.NyxIdApproval is not null ||
            cardAction.AgentRunApproval is not null)
        {
            return false;
        }

        text = string.IsNullOrWhiteSpace(cardAction.SubmittedValue)
            ? $"[card_action] {cardAction.ActionId}"
            : $"[card_action] {cardAction.ActionId}: {cardAction.SubmittedValue}";
        return true;
    }

    private static InboundMessage WithText(InboundMessage inbound, string text) =>
        new()
        {
            Platform = inbound.Platform,
            ConversationId = inbound.ConversationId,
            SenderId = inbound.SenderId,
            SenderName = inbound.SenderName,
            Text = text,
            MessageId = inbound.MessageId,
            ChatType = inbound.ChatType,
            OutboundDelivery = inbound.OutboundDelivery?.Clone(),
            TransportExtras = inbound.TransportExtras?.Clone(),
            CardAction = inbound.CardAction?.Clone(),
            Extra = inbound.Extra,
        };

    private static ChannelInboundEvent ToInboundEvent(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        InboundMessage inbound)
    {
        // Refactor (v1/issue1466-first):
        //   Old: ChannelInboundEvent copied userAccessToken into registration_token.
        //   New: inbound durable facts carry only stable routing facts.
        //   Principle: runtime credentials flow through transient context, not proto facts.
        var inboundEvent = new ChannelInboundEvent
        {
            Text = inbound.Text,
            SenderId = inbound.SenderId,
            SenderName = inbound.SenderName,
            ConversationId = inbound.ConversationId,
            MessageId = inbound.MessageId ?? string.Empty,
            ChatType = inbound.ChatType ?? string.Empty,
            Platform = inbound.Platform,
            RegistrationId = registration.Id,
            RegistrationScopeId = registration.ScopeId,
            NyxProviderSlug = registration.NyxProviderSlug,
        };

        foreach (var pair in inbound.Extra)
            inboundEvent.Extra[pair.Key] = pair.Value;

        return inboundEvent;
    }

    private async Task<NeedsLlmReplyEvent> BuildLlmReplyRequestAsync(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        ChannelInboundEvent inboundEvent,
        ConversationTurnRuntimeContext runtimeContext,
        ResolvedSenderBinding? senderBinding,
        CancellationToken ct,
        bool allowDefaultSkillRouting = false)
    {
        var allowSkillInvocationPrompt = _identityBindingQueryPort is null || senderBinding is not null;
        // Registration-level channel→skill binding: a plain text message on a bound bot runs the
        // bound Ornn skill deterministically with the message as its arguments. Only the plain-text
        // turn opts in — card actions continue their own conversations — and the same sender gate
        // as explicit skill triggers applies (unbound senders have tool dispatch disabled).
        var defaultSkillName = allowDefaultSkillRouting && allowSkillInvocationPrompt
            ? NormalizeOptional(registration.DefaultSkillName)
            : null;
        var requestActivity = BuildLlmRequestActivity(
            activity,
            inboundEvent.Text,
            inboundEvent.Platform,
            allowSkillInvocationPrompt,
            defaultSkillName);
        // Stamp the inbound bot's outbound proxy slug onto the request activity so the deferred
        // reply run (and its CardKit/im streaming sender) proxies through the bot that RECEIVED
        // this turn, not the process-wide default. Without this, the singleton Lark clients route
        // every card reply through the generic `api-lark-bot` slug, so a DM to one bot is answered
        // by a sibling bot under the same NyxID account. Typed TransportExtras field, populated here
        // where the matched registration is in hand (mirrors NyxRegistrationScopeId at ingress).
        var inboundProviderSlug = NormalizeOptional(registration.NyxProviderSlug);
        if (inboundProviderSlug is not null)
        {
            requestActivity.TransportExtras ??= new TransportExtras();
            requestActivity.TransportExtras.NyxProviderSlug = inboundProviderSlug;
        }
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = activity.Id,
            // Refactor (iter98/cluster-002): Old=correlation_id doubled as run identity; New=run_id is explicit before persistence/dispatch.
            RunId = AgentRunId.New().Value,
            RegistrationId = registration.Id,
            Activity = requestActivity,
            // Refactor (iter394/cluster-issue-394-design): Old pattern: runner filled a canonical TargetActorId placeholder. New principle: ConversationGAgent stamps its owning actor id before persistence/dispatch.
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Carry the relay reply credential through the run command as transient command-only
        // fields. ConversationGAgent strips these before persisting NeedsLlmReplyEvent;
        // AgentRunGAgent echoes them into the LlmReplyReadyEvent so the
        // outbound reply does not depend on the actor's in-memory token dict surviving
        // deactivation.
        if (runtimeContext.NyxRelayReplyToken is { } token &&
            token.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            request.ReplyToken = token.ReplyToken;
            request.ReplyTokenExpiresAtUnixMs = token.ExpiresAtUtc.ToUnixTimeMilliseconds();
        }

        var replyChannelContext = await BuildReplyChannelContextAsync(inboundEvent, activity, runtimeContext, ct);
        var replyMetadata = replyChannelContext.Metadata;
        foreach (var pair in replyMetadata)
            request.Metadata[pair.Key] = pair.Value;

        // Thread the bot's registration scope + channel identity into the deferred LLM-reply tool
        // context, mirroring the direct-reply BuildAgentBuilderToolContext. Without this, a plain
        // (non-`::`) automation turn leaves Caller.ScopeId empty, so scope-scoped tools such as
        // scheduled_agent_creator fail with "scope_id_unavailable". ToToolContext only overlays
        // credentials/routing downstream, so these typed fields survive to tool execution.
        request.ToolContext = (AgentToolExecutionContextMapper.FromPayload(request.ToolContext) with
        {
            Caller = new AgentToolCallerContext(
                inboundEvent.RegistrationScopeId,
                inboundEvent.RegistrationScopeId,
                inboundEvent.MessageId,
                senderBinding?.OwnerScopeId),
            Channel = new AgentToolChannelContext(
                inboundEvent.Platform,
                inboundEvent.SenderId,
                inboundEvent.RegistrationScopeId,
                inboundEvent.MessageId,
                NormalizeOptional(activity.TransportExtras?.NyxPlatformMessageId),
                null,
                BuildWorkflowResultDeliveryCredential(registration),
                NormalizeOptional(registration.Id),
                replyChannelContext.IdentityHints),
            ExternalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(replyMetadata),
            ExecutionOwner = AgentToolExecutionOwners.ChannelRegistration(registration.Id),
        }).ToPayload();

        if (TryBuildSkillRecoveryContext(inboundEvent.Text, inboundEvent.Platform, defaultSkillName, out var skillRecovery))
        {
            request.ToolContext = (AgentToolExecutionContextMapper.FromPayload(request.ToolContext) with
            {
                SkillRecovery = skillRecovery,
            }).ToPayload();
            _logger.LogInformation(
                "LLM reply request includes skill recovery: activity={ActivityId}, command={Command}, primarySkill={PrimarySkill}, requireInitialSearch={RequireInitialSearch}, defaultSkillName={DefaultSkillName}, senderBindingFound={SenderBindingFound}",
                activity.Id,
                skillRecovery.CommandName,
                skillRecovery.PrimarySkillName,
                skillRecovery.RequireInitialOrnnSearch,
                defaultSkillName ?? string.Empty,
                senderBinding is not null);
        }
        else
        {
            _logger.LogInformation(
                "LLM reply request has no skill recovery: activity={ActivityId}, allowSkillInvocationPrompt={AllowSkillInvocationPrompt}, defaultSkillName={DefaultSkillName}, senderBindingFound={SenderBindingFound}",
                activity.Id,
                allowSkillInvocationPrompt,
                defaultSkillName ?? string.Empty,
                senderBinding is not null);
        }

        request.LlmControl = (await BuildOwnerLlmControlAsync(
                inboundEvent,
                LLMControlContextMapper.FromPayload(request.LlmControl),
                ct)
            .ConfigureAwait(false)).ToPayload();

        // Tag the request with the sender's binding-id and a short-lived token
        // so the downstream reply generator can try the sender's own LLM
        // route first. Missing token/binding is not an error: the generator
        // falls back to the bot owner's upstream-pinned LLM config.
        if (senderBinding is not null)
        {
            // Carry the binding-id AND the external-subject tenant as identity
            // facts (not credentials). Both survive the ConversationGAgent
            // transient-credential strip, so the deferred reply run can rebuild
            // the exact ExternalSubjectRef and re-mint a fresh sender token by
            // binding id (the synchronously-minted token below is stripped
            // before persistence, so the deferred run cannot reuse it).
            var senderTenant = NormalizeOptional(senderBinding.Subject.Tenant);
            request.ToolContext = (AgentToolExecutionContextMapper.FromPayload(request.ToolContext) with
            {
                SenderBinding = new AgentToolSenderBindingContext(
                    senderBinding.BindingId,
                    NyxUserId: null,
                    SenderTenant: senderTenant),
                NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                    senderBinding.Subject.Platform,
                    senderBinding.Subject.Tenant,
                    senderBinding.Subject.ExternalUserId),
                Caller = AgentToolExecutionContextMapper.FromPayload(request.ToolContext).Caller with
                {
                    OwnerScopeId = senderBinding.OwnerScopeId,
                },
            }).ToPayload();
            var senderAccessToken = await TryIssueSenderLlmAccessTokenAsync(senderBinding.Subject, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(senderAccessToken))
            {
                var currentControl = LLMControlContextMapper.FromPayload(request.LlmControl);
                request.LlmControl = new LLMControlContext(
                    currentControl.NyxIdAccessToken,
                    currentControl.NyxIdOrgToken,
                    senderAccessToken.Trim(),
                    currentControl.ModelOverride,
                    currentControl.NyxIdRoutePreference,
                    currentControl.MaxToolRoundsOverride,
                    currentControl.UserMemoryPrompt).ToPayload();
                var senderNyxUserId = await TryResolveSenderNyxUserIdAsync(senderAccessToken, senderBinding.Subject, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(senderNyxUserId))
                {
                    request.ToolContext = (AgentToolExecutionContextMapper.FromPayload(request.ToolContext) with
                    {
                        SenderBinding = new AgentToolSenderBindingContext(
                            senderBinding.BindingId,
                            senderNyxUserId.Trim(),
                            senderTenant),
                        Caller = AgentToolExecutionContextMapper.FromPayload(request.ToolContext).Caller with
                        {
                            OwnerScopeId = NormalizeOptional(senderBinding.OwnerScopeId),
                        },
                    }).ToPayload();
                }
            }
        }

        return request;
    }

    private bool TryBuildSkillRecoveryContext(
        string? text,
        string? platform,
        string? defaultSkillName,
        out AgentSkillRecoveryContext context)
    {
        context = AgentSkillRecoveryContext.Empty;
        if (!TryResolveSkillInvocationTrigger(
                text,
                platform,
                defaultSkillName,
                out var trigger,
                out var viaDefaultSkillBinding))
            return false;

        if (trigger.IsDiscovery)
        {
            context = AgentSkillRecoveryContextBuilder.FromTrigger(trigger);
            return true;
        }

        var normalizedCommand = trigger.Name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCommand))
            return false;

        if (trigger.TriggerToken == "/" &&
            (LocalSlashCommands.Contains(normalizedCommand) ||
             ResolveSlashCommandHandler(normalizedCommand) is not null))
        {
            return false;
        }

        context = AgentSkillRecoveryContextBuilder.FromTrigger(trigger) with
        {
            CommandName = normalizedCommand,
            PrimarySkillName = normalizedCommand,
            IsolatePriorConversationHistory = !viaDefaultSkillBinding,
        };
        return true;
    }

    private async Task<LLMControlContext> BuildOwnerLlmControlAsync(
        ChannelInboundEvent inboundEvent,
        LLMControlContext control,
        CancellationToken ct)
    {
        return await OwnerLlmConfigApplier.ApplyAsync(
                control,
                inboundEvent.RegistrationScopeId,
                _ownerLlmConfigSource,
                _logger,
                actorLabel: "Channel turn runner",
                actorId: inboundEvent.MessageId,
                ct)
            .ConfigureAwait(false);
    }

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    private ChatActivity BuildLlmRequestActivity(
        ChatActivity activity,
        string? inboundText,
        string? platform,
        bool allowSkillInvocationPrompt,
        string? defaultSkillName = null)
    {
        var requestActivity = activity.Clone();
        if (requestActivity.Content is null)
            return requestActivity;

        requestActivity.Content.Text = inboundText ?? string.Empty;
        if (allowSkillInvocationPrompt && TryBuildSkillInvocationPrompt(inboundText, platform, defaultSkillName, out var prompt))
            requestActivity.Content.Text = prompt;

        return requestActivity;
    }

    private bool TryBuildSkillInvocationPrompt(string? text, string? platform, string? defaultSkillName, out string prompt)
    {
        prompt = string.Empty;
        if (!TryResolveSkillInvocationTrigger(text, platform, defaultSkillName, out var trigger, out var viaDefaultSkillBinding) ||
            trigger.IsDiscovery)
        {
            return false;
        }

        // Refactor (iter1/cluster-issue1553): Old pattern: hardcoded /daily skill name. New principle: generic skill discovery, no skill-name in routing logic.
        return TryBuildSlashSkillDiscoveryPrompt(trigger, viaDefaultSkillBinding, out prompt);
    }

    // Explicit "/<skill>"/"::<skill>" triggers always win; the registration-level default-skill
    // binding only claims plain text, turning the whole message into the skill's arguments.
    private static bool TryResolveSkillInvocationTrigger(
        string? text,
        string? platform,
        string? defaultSkillName,
        out SkillInvocationTrigger trigger,
        out bool viaDefaultSkillBinding)
    {
        viaDefaultSkillBinding = false;
        if (SkillInvocationTriggerParser.TryParse(text, platform, out trigger))
            return true;

        if (string.IsNullOrWhiteSpace(defaultSkillName) || string.IsNullOrWhiteSpace(text))
            return false;

        var messageText = text.Trim();
        trigger = new SkillInvocationTrigger(
            Name: defaultSkillName,
            Arguments: messageText,
            IsDiscovery: false,
            OriginalText: messageText,
            TriggerToken: "/",
            Platform: string.IsNullOrWhiteSpace(platform) ? "default" : platform.Trim());
        viaDefaultSkillBinding = true;
        return true;
    }

    private bool TryBuildSlashSkillDiscoveryPrompt(
        SkillInvocationTrigger trigger,
        bool viaDefaultSkillBinding,
        out string prompt)
    {
        prompt = string.Empty;
        var commandName = trigger.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandName) ||
            (trigger.TriggerToken == "/" &&
             (LocalSlashCommands.Contains(commandName) ||
              ResolveSlashCommandHandler(commandName) is not null)))
        {
            return false;
        }

        var normalizedCommand = commandName.Trim();
        var skillQuery = normalizedCommand.TrimStart('/');
        if (string.IsNullOrWhiteSpace(skillQuery))
            return false;

        var argsJson = JsonSerializer.Serialize(trigger.Arguments);
        var originalJson = JsonSerializer.Serialize(trigger.OriginalText);
        var triggerLabel = trigger.TriggerToken == "/" ? "/" : trigger.TriggerToken;
        var invocationLine = viaDefaultSkillBinding
            ? $"This channel bot is bound to the `{normalizedCommand}` skill: every plain inbound message runs that skill with the full message text as its arguments.\n"
            : $"The user invoked the `{triggerLabel}{normalizedCommand}` skill trigger.\n";
        var useSkillInstruction = trigger.MountWorkflowsRequested
            ? "The user explicitly requested mounting this skill's workflows. Use a matching `use_skill` mount preview already present in this turn; otherwise call `use_skill` with this skill name, the exact command arguments, and `mount_workflows=true`. The first call is a read-only preview. When it returns `workflow_mount_confirmation_token`, call `use_skill` again with the same skill, args, `mount_workflows=true`, and that exact token so the mutating call enters durable approval. Do not claim the workflows are mounted until the matching successful mutating receipt is present.\n"
            : "Use a matching successful `use_skill` result already present in this turn. If none is present, call `use_skill` with this skill name and the exact command arguments; omit `mount_workflows` because loading instructions is read-only and must not mutate scope workflows.\n";
        prompt =
            invocationLine +
            "This command is not handled by Aevatar's local relay commands. Treat it as an Ornn skill-backed command, not an open-ended chat answer.\n" +
            useSkillInstruction +
            $"Follow those skill instructions exactly, with `args` = {argsJson}, until the command's final result is ready.\n" +
            "Stick to the data sources the loaded skill names. Do NOT invent repository/path guesses, do NOT call `/api/v1/skills/.../files` (skill files are already inlined in the `use_skill` response above), and do NOT fall back to generic `nyxid_proxy` discovery when the loaded skill did not point you there.\n" +
            "If no matching skill was actually loaded above, or every matching skill fails to load, give one concise actionable failure that names the command and the Ornn lookup/load problem.\n" +
            "If a loaded skill leaves any workflow step, source layout, API contract, or required capability ambiguous, call `ornn_search_skills` with the concrete blocker and then `use_skill` the best matching skill before trying generic proxy discovery or path guessing.\n" +
            "Do not narrate intermediate work, path guesses, or partial findings as the user-visible reply.\n" +
            "The only final user-visible answer should be the completed command result or a concise actionable failure after the required tool/skill recovery attempts have been exhausted.\n" +
            $"Original command: {originalJson}";
        return true;
    }

    private async Task<string?> TryIssueSenderLlmAccessTokenAsync(
        ExternalSubjectRef subject,
        CancellationToken ct)
    {
        var broker = _capabilityBroker;
        if (broker is null)
            return null;

        try
        {
            var handle = await broker
                .IssueShortLivedAsync(
                    subject,
                    new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy },
                    ct)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(handle.AccessToken)
                ? null
                : handle.AccessToken.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BindingRevokedException ex)
        {
            // Grant is gone upstream (NyxID invalid_grant). Reconcile the local
            // binding (best-effort, off the reply path) so /whoami shows unbound
            // and /init lets the sender re-bind, then fall back to owner config.
            _logger.LogWarning(
                ex,
                "Sender NyxID binding revoked at NyxID; reconciling local binding and falling back to bot owner LLM config. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            TriggerBindingReconcile(subject);
            return null;
        }
        catch (BindingServiceAccessMismatchException ex)
        {
            _logger.LogWarning(
                ex,
                "Sender NyxID binding lacks a required service; preserving it until /init service authorization renewal succeeds. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to issue sender NyxID LLM token; falling back to bot owner LLM config. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return null;
        }
    }

    private void TriggerBindingReconcile(
        ExternalSubjectRef subject,
        string reason = "nyx_invalid_grant")
    {
        var reconciler = _bindingRevocationReconciler;
        if (reconciler is null)
            return;

        var subjectSnapshot = subject.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                await reconciler
                    .ReconcileRevokedAsync(subjectSnapshot, reason, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort self-heal; reconcile failures must never surface on
                // the reply path. The reconciler logs its own dispatch failures;
                // this only catches unexpected faults so the fire-and-forget task
                // never escapes unobserved.
                _logger.LogWarning(ex, "Binding reconcile after invalid_grant failed (best-effort, ignored).");
            }
        });
    }

    private async Task<string?> TryResolveSenderNyxUserIdAsync(
        string senderAccessToken,
        ExternalSubjectRef subject,
        CancellationToken ct)
    {
        var resolver = _nyxIdCurrentUserResolver;
        if (resolver is null || string.IsNullOrWhiteSpace(senderAccessToken))
            return null;

        try
        {
            var nyxUserId = await resolver.ResolveCurrentUserIdAsync(senderAccessToken, ct).ConfigureAwait(false);
            return NormalizeOptional(nyxUserId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve sender NyxID user id from short-lived token; preserving typed owner scope and continuing without sender NyxID user id enrichment. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return null;
        }
    }

    private static string ResolveRoutingConversationId(ConversationReference? conversation)
    {
        if (conversation is null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(conversation.Partition))
            return conversation.Partition;

        if (conversation.Scope == ConversationScope.DirectMessage)
            return string.Empty;

        return ResolveLastCanonicalSegment(conversation.CanonicalKey);
    }

    private static string ResolveLastCanonicalSegment(string? canonicalKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalKey))
            return string.Empty;

        var parts = canonicalKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? string.Empty : parts[^1];
    }

    private static string ResolveChatType(ConversationReference? conversation, ActivityType activityType = ActivityType.Message)
    {
        if (activityType == ActivityType.CardAction)
            return "card_action";

        return ResolveConversationChatType(conversation);
    }

    private static string ResolveConversationChatType(ConversationReference? conversation)
    {
        return conversation?.Scope switch
        {
            ConversationScope.DirectMessage => "p2p",
            ConversationScope.Group => "group",
            ConversationScope.Channel => "channel",
            ConversationScope.Thread => "thread",
            _ => "conversation",
        };
    }

    private static bool HasRelayDelivery(InboundMessage inbound) =>
        inbound.OutboundDelivery is
        {
            ReplyMessageId.Length: > 0,
            CorrelationId.Length: > 0,
        };

    private static string? ResolveRelayReplyToken(
        OutboundDeliveryContext relayDelivery,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var tokenContext = runtimeContext.NyxRelayReplyToken;
        if (tokenContext is null || tokenContext.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return null;

        if (!string.Equals(
                NormalizeOptional(relayDelivery.CorrelationId),
                NormalizeOptional(tokenContext.CorrelationId),
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.Equals(
                NormalizeOptional(relayDelivery.ReplyMessageId),
                NormalizeOptional(tokenContext.ReplyMessageId),
                StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeOptional(tokenContext.ReplyToken);
    }

    // Refactor (iter17/cluster-038):
    //   Old pattern: channel runner resolved the Nyx user token only from ChatActivity.TransportExtras, forcing secrets into persisted activity clones.
    //   New principle: sanitized activities may omit the token; same-activation relay turns read it from ConversationTurnRuntimeContext.
    private static string? ResolveUserAccessToken(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext) =>
        NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken) ??
        NormalizeOptional(runtimeContext.NyxUserAccessToken);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string ResolveRelayPlatform(InboundMessage inbound, ConversationReference? conversation)
    {
        var platform = !string.IsNullOrWhiteSpace(inbound.TransportExtras?.NyxPlatform)
            ? inbound.TransportExtras.NyxPlatform
            : !string.IsNullOrWhiteSpace(inbound.Platform)
                ? inbound.Platform
                : conversation?.Channel?.Value ?? string.Empty;

        return string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase)
            ? "lark"
            : platform;
    }

    // Group-chat admission: returns true when an inbound group/channel/thread message does NOT
    // address the bot and should be dropped silently. The gate is opt-in — it stays inert until an
    // ILarkBotIdentityResolver is wired, so a missing DI registration degrades to the legacy
    // "engage everything" behavior rather than going silent. Slash commands and replies-to-the-bot
    // are free signals checked before any network call; only a message that @-mentions someone
    // forces an on-demand bot/v3/info resolve to learn whether that mention is the bot.
    private async Task<bool> ShouldIgnoreUnaddressedGroupMessageAsync(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        if (_botIdentityResolver is null)
            return false;

        // Card actions (button clicks) are explicit interactions; never gate them. DMs are 1:1 and
        // always addressed. Only Lark group-like message activities are subject to the gate.
        if (activity.Type != ActivityType.Message)
            return false;

        if (!IsLarkActivity(activity, registration))
            return false;

        if (!IsGroupLikeScope(activity.Conversation?.Scope))
            return false;

        if (LooksLikeSlashCommand(activity.Content?.Text))
            return false;

        if (runtimeContext.IsReplyToBot)
            return false;

        // With no @-mention at all the message cannot name the bot, so ignore without a network
        // call. Otherwise resolve the bot's own open_id and engage only if it is among the mentions.
        if (activity.Mentions.Count == 0)
            return true;

        var accessToken = ResolveUserAccessToken(activity, runtimeContext);
        var providerSlug = NormalizeOptional(registration.NyxProviderSlug);
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(providerSlug))
            return false; // identity unknowable -> fail open (engage)

        var botOpenId = await _botIdentityResolver.ResolveBotOpenIdAsync(providerSlug!, accessToken!, ct);
        if (string.IsNullOrWhiteSpace(botOpenId))
            return false; // resolution failed -> fail open (engage)

        var botMentioned = activity.Mentions.Any(mention =>
            string.Equals(mention.CanonicalId, botOpenId, StringComparison.Ordinal));
        return !botMentioned;
    }

    private static bool IsGroupLikeScope(ConversationScope? scope) =>
        scope is ConversationScope.Group or ConversationScope.Channel or ConversationScope.Thread;

    private static bool LooksLikeSlashCommand(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('/');

    private static bool IsLarkActivity(ChatActivity activity, ChannelBotRegistrationEntry registration)
    {
        var platform = NormalizeOptional(activity.TransportExtras?.NyxPlatform)
            ?? NormalizeOptional(registration.Platform)
            ?? NormalizeOptional(activity.ChannelId?.Value);
        return string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase);
    }

    // Lark reaction emoji_type for "hands typing on keyboard" — added immediately on inbound
    // so the user sees the bot is working before the LLM reply lands. After a reply succeeds,
    // the reaction is cleared instead of replaced with DONE because DONE reads as task completion,
    // while a chat reply can be an intermediate progress update.
    private const string TypingReactionEmojiType = "Typing";

    private async Task TrySendImmediateLarkReactionAsync(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        if (!ShouldSendImmediateLarkReaction(activity, registration, out var accessToken, out var providerSlug, out var platformMessageId))
            return;

        try
        {
            var response = await _nyxClient.ProxyRequestAsync(
                accessToken!,
                providerSlug!,
                $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions",
                "POST",
                $$$"""{"reaction_type":{"emoji_type":"{{{TypingReactionEmojiType}}}"}}""",
                null,
                ct);

            var classification = ClassifyRelayProxyResponse(response);
            if (classification.IsError)
            {
                if (classification.Kind == ChannelRelayProxyResponseKind.PermissionDenied)
                {
                    // The bot is missing reaction permission on Lark — a
                    // tenant-level config issue that recurs on every inbound
                    // message until ops fixes the app scope. Log at Debug so
                    // it stays discoverable when the channel is opted into
                    // verbose logging without spamming Warnings on every turn.
                    _logger.LogDebug(
                        "Immediate Lark typing reaction skipped (missing reaction scope): provider={ProviderSlug}, message={MessageId}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        classification.Detail);
                }
                else
                {
                    // Anything else is a real signal that should stay at Warning
                    // so provider behavior changes remain visible.
                    _logger.LogWarning(
                        "Immediate Lark typing reaction failed: provider={ProviderSlug}, message={MessageId}, providerErrorCode={ProviderErrorCode}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        classification.ProviderErrorCode,
                        classification.Detail);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Immediate Lark typing reaction threw: provider={ProviderSlug}, message={MessageId}",
                providerSlug,
                platformMessageId);
        }
    }

    // Direct-reply paths (TryHandleAgentBuilderAsync) can complete a slash-command reply faster
    // than the typing POST takes to land in Lark, leaving the clear GET to find no Typing reaction
    // to delete and the orphaned typing reaction to materialize after the clear already ran.
    // Awaiting (with a short cap) the typing task before the GET closes that race. The cap protects
    // against a hung POST stalling the clear forever. The deferred-LLM and streaming paths skip this
    // guard because their reply latency dwarfs the typing POST and so cannot race.
    private async Task AwaitTypingReactionThenClearAsync(
        Task typingReactionTask,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        try
        {
            await typingReactionTask.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            _logger.LogDebug(
                "Lark typing reaction task did not complete within timeout before clear; proceeding anyway");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lark typing reaction task failed before clear; proceeding with clear");
        }

        await TryClearTypingReactionAsync(inbound, registration, ct);
    }

    // After a successful reply, remove the bot's "Typing" reaction. Uses list-based discovery (filter by
    // emoji_type=Typing AND operator_type=app) instead of caching the immediate reaction's
    // reaction_id locally — the runner is a singleton and cross-turn state on it would violate the
    // "中间层进程内缓存作为事实源" rule. Filtering on operator_type=app avoids deleting any user
    // who happened to add the same Typing reaction.
    private async Task TryClearTypingReactionAsync(
        InboundMessage inbound,
        ChannelBotRegistrationEntry? registration,
        CancellationToken ct)
    {
        if (registration is null)
            return;

        if (!ShouldClearTypingReaction(inbound, registration, out var accessToken, out var providerSlug, out var platformMessageId))
            return;

        try
        {
            var reactionIds = new List<string>();
            string? pageToken = null;
            // Bound the iteration so a misbehaving Lark response (e.g. always-true `has_more`)
            // can't loop the clear forever. 10 pages × 50 per page = 500 Typing reactions on a
            // single message — orders of magnitude more than realistic, since this list is
            // already scoped to one emoji_type and the bot only adds Typing once per inbound.
            const int MaxListPages = 10;
            for (var page = 0; page < MaxListPages; page++)
            {
                var pathQuery = $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions?reaction_type={TypingReactionEmojiType}&page_size=50";
                if (pageToken is not null)
                    pathQuery += $"&page_token={Uri.EscapeDataString(pageToken)}";

                var listResponse = await _nyxClient.ProxyRequestAsync(
                    accessToken!,
                    providerSlug!,
                    pathQuery,
                    "GET",
                    body: null,
                    extraHeaders: null,
                    ct);

                var listClassification = ClassifyRelayProxyResponse(listResponse);
                if (listClassification.IsError)
                {
                    _logger.LogDebug(
                        "Lark typing reaction list failed; skipping clear: provider={ProviderSlug}, message={MessageId}, page={Page}, providerErrorCode={ProviderErrorCode}, detail={Detail}",
                        providerSlug,
                        platformMessageId,
                        page,
                        listClassification.ProviderErrorCode,
                        listClassification.Detail);
                    return;
                }

                var (idsOnPage, nextPageToken) = ParseAppReactionsPage(listResponse);
                reactionIds.AddRange(idsOnPage);
                if (string.IsNullOrWhiteSpace(nextPageToken))
                {
                    pageToken = null;
                    break;
                }
                pageToken = nextPageToken;
            }

            foreach (var reactionId in reactionIds)
            {
                try
                {
                    var deleteResponse = await _nyxClient.ProxyRequestAsync(
                        accessToken!,
                        providerSlug!,
                        $"/open-apis/im/v1/messages/{Uri.EscapeDataString(platformMessageId!)}/reactions/{Uri.EscapeDataString(reactionId)}",
                        "DELETE",
                        body: null,
                        extraHeaders: null,
                        ct);

                    var deleteClassification = ClassifyRelayProxyResponse(deleteResponse);
                    if (deleteClassification.IsError)
                    {
                        _logger.LogDebug(
                            "Lark typing reaction delete failed: provider={ProviderSlug}, message={MessageId}, reaction={ReactionId}, providerErrorCode={ProviderErrorCode}, detail={Detail}",
                            providerSlug,
                            platformMessageId,
                            reactionId,
                            deleteClassification.ProviderErrorCode,
                            deleteClassification.Detail);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Lark typing reaction delete threw: provider={ProviderSlug}, message={MessageId}, reaction={ReactionId}",
                        providerSlug,
                        platformMessageId,
                        reactionId);
                }
            }

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lark typing reaction clear threw: provider={ProviderSlug}, message={MessageId}",
                providerSlug,
                platformMessageId);
        }
    }

    private static (IReadOnlyList<string> AppReactionIds, string? NextPageToken) ParseAppReactionsPage(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return (Array.Empty<string>(), null);

        try
        {
            return ExtractAppReactionsPage(response);
        }
        catch (JsonException)
        {
            return (Array.Empty<string>(), null);
        }
    }

    private static (List<string> AppReactionIds, string? NextPageToken) ExtractAppReactionsPage(string response)
    {
        var ids = new List<string>();
        string? nextPageToken = null;

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (ids, null);

        if (!root.TryGetProperty("data", out var dataProp) || dataProp.ValueKind != JsonValueKind.Object)
            return (ids, null);

        // Pin pagination to has_more=true. Following page_token unconditionally would let a Lark
        // response that returns a stale token alongside has_more=false re-fetch the same page
        // until the safety cap fires.
        var hasMore = dataProp.TryGetProperty("has_more", out var hasMoreProp) &&
                      hasMoreProp.ValueKind == JsonValueKind.True;
        if (hasMore &&
            dataProp.TryGetProperty("page_token", out var pageTokenProp) &&
            pageTokenProp.ValueKind == JsonValueKind.String)
        {
            var token = pageTokenProp.GetString();
            if (!string.IsNullOrWhiteSpace(token))
                nextPageToken = token;
        }

        if (!dataProp.TryGetProperty("items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array)
            return (ids, nextPageToken);

        foreach (var item in itemsProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            // Only delete reactions added by the bot itself (operator_type=app); leave any
            // user-added Typing reactions alone so the clear doesn't accidentally erase them.
            if (!item.TryGetProperty("operator", out var operatorProp) ||
                operatorProp.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!operatorProp.TryGetProperty("operator_type", out var operatorTypeProp) ||
                operatorTypeProp.ValueKind != JsonValueKind.String ||
                !string.Equals(operatorTypeProp.GetString(), "app", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("reaction_id", out var reactionIdProp) ||
                reactionIdProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var reactionId = reactionIdProp.GetString();
            if (!string.IsNullOrWhiteSpace(reactionId))
                ids.Add(reactionId);
        }

        return (ids, nextPageToken);
    }

    private static bool ShouldClearTypingReaction(
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        out string? accessToken,
        out string? providerSlug,
        out string? platformMessageId)
    {
        accessToken = null;
        providerSlug = null;
        platformMessageId = null;

        var platform = NormalizeOptional(inbound.TransportExtras?.NyxPlatform) ??
                       NormalizeOptional(registration.Platform) ??
                       NormalizeOptional(inbound.Platform);
        if (!string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        accessToken = NormalizeOptional(inbound.TransportExtras?.NyxUserAccessToken);
        providerSlug = NormalizeOptional(registration.NyxProviderSlug);
        platformMessageId = NormalizeOptional(inbound.TransportExtras?.NyxPlatformMessageId);

        return !string.IsNullOrWhiteSpace(accessToken) &&
               !string.IsNullOrWhiteSpace(providerSlug) &&
               !string.IsNullOrWhiteSpace(platformMessageId) &&
               platformMessageId.StartsWith("om_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSendImmediateLarkReaction(
        ChatActivity activity,
        ChannelBotRegistrationEntry registration,
        out string? accessToken,
        out string? providerSlug,
        out string? platformMessageId)
    {
        accessToken = null;
        providerSlug = null;
        platformMessageId = null;

        if (activity.Type != ActivityType.Message)
            return false;

        var platform = NormalizeOptional(activity.TransportExtras?.NyxPlatform) ??
                       NormalizeOptional(registration.Platform) ??
                       NormalizeOptional(activity.ChannelId?.Value);
        if (!string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        accessToken = NormalizeOptional(activity.TransportExtras?.NyxUserAccessToken);
        providerSlug = NormalizeOptional(registration.NyxProviderSlug);
        platformMessageId = NormalizeOptional(activity.TransportExtras?.NyxPlatformMessageId);

        return !string.IsNullOrWhiteSpace(accessToken) &&
               !string.IsNullOrWhiteSpace(providerSlug) &&
               !string.IsNullOrWhiteSpace(platformMessageId) &&
               platformMessageId.StartsWith("om_", StringComparison.OrdinalIgnoreCase);
    }

    private static ConversationTurnResult ToRelayFailure(EmitResult emit)
    {
        var errorCode = string.IsNullOrWhiteSpace(emit.ErrorCode) ? "relay_reply_rejected" : emit.ErrorCode;
        var errorMessage = string.IsNullOrWhiteSpace(emit.ErrorMessage)
            ? "Nyx relay reply rejected."
            : emit.ErrorMessage;

        return errorCode switch
        {
            // The reply token has already been consumed (single-use). Re-running the inbound
            // turn at grain level (`ConversationGAgent.HandleInboundTurnTransientFailureAsync`)
            // would replay the same token and get `401 Reply token already used` forever, so
            // route to PermanentFailure to short-circuit the retry queue. The user-facing
            // recovery is to send a fresh inbound message which carries a fresh token.
            "relay_reply_token_consumed" or
            "reply_token_missing_or_expired" or "missing_reply_message_id" or "empty_reply" =>
                ConversationTurnResult.PermanentFailure(errorCode, errorMessage),
            _ when emit.RetryAfterTimeSpan is { } retryAfter =>
                ConversationTurnResult.TransientFailure(errorCode, errorMessage, retryAfter),
            _ => ConversationTurnResult.TransientFailure(errorCode, errorMessage),
        };
    }

    private ChannelRelayProxyResponseClassification ClassifyRelayProxyResponse(string? response) =>
        _relayProxyResponseClassifier?.Classify(response) ??
        ChannelRelayProxyResponseClassification.Success();

    private static ChannelId ResolveRelayChannel(InboundMessage inbound, ConversationReference? conversation) =>
        ChannelId.From(ResolveRelayPlatform(inbound, conversation));

    private static bool HasContent(MessageContent content) =>
        !string.IsNullOrWhiteSpace(content.Text) ||
        HasInteractiveContent(content) ||
        content.Attachments.Count > 0;

    private static bool HasInteractiveContent(MessageContent content) =>
        content.Actions.Count > 0 || content.Cards.Count > 0;

    private static string NormalizeReplyText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(no content)" : text.Trim();

    private static string FormatReplyTextForPlatform(string? platform, string text) =>
        string.Equals(platform?.Trim(), "lark", StringComparison.OrdinalIgnoreCase)
            ? LarkJsonTableFormatter.FormatAsKeyValueText(text)
            : text;

    private static bool IsLarkChannel(ChannelId channel) =>
        string.Equals(channel.Value, "lark", StringComparison.OrdinalIgnoreCase);

    private static ConversationTurnResult BuildRelaySentResult(
        string? sentActivityId,
        string sentActivitySeed,
        MessageContent outbound,
        OutboundDeliveryContext relayDelivery) =>
        ConversationTurnResult.Sent(
            sentActivityId: string.IsNullOrWhiteSpace(sentActivityId)
                ? $"direct-reply:{sentActivitySeed}"
                : sentActivityId,
            outbound: outbound.Clone(),
            authPrincipal: "bot",
            outboundDelivery: new OutboundDeliveryContext
            {
                ReplyMessageId = relayDelivery.ReplyMessageId,
                CorrelationId = relayDelivery.CorrelationId,
            });
}
