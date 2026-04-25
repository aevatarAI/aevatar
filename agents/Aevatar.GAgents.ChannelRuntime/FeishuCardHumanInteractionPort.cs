using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Delivers workflow human-interaction suspensions and resolutions as Lark interactive cards
/// via the NyxID proxy. Card construction runs through <see cref="LarkMessageComposer"/> so the
/// outbound shape and Lark-native card schema stay owned by the composer; this port only knows
/// the <see cref="MessageContent"/> intent and the NyxID proxy transport (a proactive outbound
/// send to a user-scoped conversation, distinct from relay-reply traffic).
/// </summary>
public sealed class FeishuCardHumanInteractionPort : IHumanInteractionPort
{
    private readonly IUserAgentCatalogRuntimeQueryPort _agentRegistryQueryPort;
    private readonly NyxIdApiClient _nyxIdApiClient;
    private readonly LarkMessageComposer _composer;
    private readonly ILogger<FeishuCardHumanInteractionPort> _logger;

    public FeishuCardHumanInteractionPort(
        IUserAgentCatalogRuntimeQueryPort agentRegistryQueryPort,
        NyxIdApiClient nyxIdApiClient,
        LarkMessageComposer composer,
        ILogger<FeishuCardHumanInteractionPort> logger)
    {
        _agentRegistryQueryPort = agentRegistryQueryPort ?? throw new ArgumentNullException(nameof(agentRegistryQueryPort));
        _nyxIdApiClient = nyxIdApiClient ?? throw new ArgumentNullException(nameof(nyxIdApiClient));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task DeliverSuspensionAsync(
        HumanInteractionRequest request,
        string deliveryTargetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await ResolveTargetAsync(deliveryTargetId, cancellationToken);
        await SendInteractiveCardMessageAsync(
            target,
            BuildCardJson(request, _composer),
            "Feishu human interaction card delivery returned empty response.",
            "Feishu human interaction card delivery failed",
            cancellationToken);

        _logger.LogInformation(
            "Delivered human interaction card: target={DeliveryTargetId}, run={RunId}, step={StepId}",
            deliveryTargetId,
            request.RunId,
            request.StepId);
    }

    public async Task DeliverApprovalResolutionAsync(
        HumanApprovalResolution resolution,
        string deliveryTargetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var target = await ResolveTargetAsync(deliveryTargetId, cancellationToken);
        await SendTextMessageAsync(
            target,
            BuildApprovalResolutionText(resolution, target),
            "Feishu approval resolution delivery returned empty response.",
            "Feishu approval resolution delivery failed",
            cancellationToken);

        if (ShouldSendApprovedContent(target, resolution))
        {
            await SendTextMessageAsync(
                target,
                resolution.ResolvedContent!,
                "Feishu approved-content delivery returned empty response.",
                "Feishu approved-content delivery failed",
                cancellationToken);
        }

        _logger.LogInformation(
            "Delivered human approval resolution text: target={DeliveryTargetId}, run={RunId}, step={StepId}, approved={Approved}",
            deliveryTargetId,
            resolution.RunId,
            resolution.StepId,
            resolution.Approved);
    }

    internal static string BuildSuspensionText(HumanInteractionRequest request)
    {
        var lines = new List<string>
        {
            ResolveTitle(request),
            request.Prompt,
            $"Run ID: {request.RunId}",
            $"Step ID: {request.StepId}",
            $"Actor ID: {request.ActorId}",
        };

        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            lines.Add(string.Empty);
            lines.Add("Current content:");
            lines.Add(request.Content!);
        }

        lines.Add(string.Empty);
        if (SupportsApproveReject(request))
        {
            lines.Add("Approve:");
            lines.Add(BuildApproveCommand(request));
            lines.Add(string.Empty);
            lines.Add("Approve with edits:");
            lines.Add(BuildApproveCommand(request, "edited_content=\"final approved content\""));
            lines.Add(string.Empty);
            lines.Add("Reject:");
            lines.Add(BuildRejectCommand(request, "feedback=\"what should change\""));
        }
        else
        {
            lines.Add("Submit response:");
            lines.Add(BuildSubmitCommand(request, "user_input=\"your response here\""));
        }

        return string.Join('\n', lines);
    }

    internal static string BuildApprovalResolutionText(
        HumanApprovalResolution resolution,
        UserAgentCatalogEntry? target = null)
    {
        var lines = new List<string>
        {
            resolution.Approved ? "Approval recorded." : "Rejection recorded.",
            $"Run ID: {resolution.RunId}",
            $"Step ID: {resolution.StepId}",
        };

        if (!string.IsNullOrWhiteSpace(resolution.Feedback))
            lines.Add($"Feedback: {resolution.Feedback}");

        if (!resolution.Approved && target is not null &&
            string.Equals(target.TemplateName, WorkflowAgentDefaults.TemplateName, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(string.Empty);
            lines.Add($"Run again: /run-agent {target.AgentId}");
            lines.Add("View agents: /agents");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Builds the Lark interactive card JSON for the suspension request by projecting it onto a
    /// <see cref="MessageContent"/> intent and delegating rendering to <see cref="LarkMessageComposer"/>.
    /// </summary>
    /// <remarks>
    /// The outbound button-value payload must stay byte-compatible with the inbound card-action
    /// parser in <see cref="Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayTransport"/>, which maps
    /// <c>content.text.value</c> into <see cref="CardActionSubmission.Arguments"/> and
    /// <c>content.text.form_value</c> into <see cref="CardActionSubmission.FormFields"/>. The
    /// correlation keys (<c>actor_id</c>, <c>run_id</c>, <c>step_id</c>, <c>approved</c>) are
    /// carried via the <c>ActionElement.Arguments</c> map and form-input names
    /// (<c>edited_content</c>, <c>user_input</c>) are carried as action ids, so
    /// <see cref="ChannelCardActionRouting"/> can rebuild the workflow resume command downstream.
    /// </remarks>
    internal static string BuildCardJson(HumanInteractionRequest request) =>
        BuildCardJson(request, new LarkMessageComposer());

    internal static string BuildCardJson(HumanInteractionRequest request, LarkMessageComposer composer)
    {
        var intent = BuildSuspensionIntent(request);
        var payload = composer.Compose(intent, BuildComposeContext());
        return payload.ContentJson;
    }

    internal static MessageContent BuildSuspensionIntent(HumanInteractionRequest request)
    {
        var intent = new MessageContent
        {
            Text = string.Empty,
            Disposition = MessageDisposition.Normal,
        };

        var headerCard = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = ResolveTitle(request),
            Text = BuildLegacyMarkdown(request),
        };
        intent.Cards.Add(headerCard);

        if (SupportsApproveReject(request))
        {
            intent.Actions.Add(BuildTextInput("edited_content", "Edited Draft (Optional)", "Paste the final draft here before approving"));
            intent.Actions.Add(BuildTextInput("user_input", "Rejection Feedback (Optional)", "Explain what should change if you reject"));
            intent.Actions.Add(BuildFormButton(
                actionId: "approve",
                label: "Approve",
                isPrimary: true,
                isDanger: false,
                request,
                approved: true));
            intent.Actions.Add(BuildFormButton(
                actionId: "reject",
                label: "Reject",
                isPrimary: false,
                isDanger: true,
                request,
                approved: false));
        }
        else
        {
            intent.Actions.Add(BuildTextInput("user_input", "Response", "Enter your response"));
            intent.Actions.Add(BuildFormButton(
                actionId: "submit",
                label: "Submit",
                isPrimary: true,
                isDanger: false,
                request,
                approved: null));
        }

        return intent;
    }

    private static ActionElement BuildTextInput(string actionId, string label, string placeholder) =>
        new()
        {
            Kind = ActionElementKind.TextInput,
            ActionId = actionId,
            Label = label,
            Placeholder = placeholder,
        };

    private static ActionElement BuildFormButton(
        string actionId,
        string label,
        bool isPrimary,
        bool isDanger,
        HumanInteractionRequest request,
        bool? approved)
    {
        var button = new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = actionId,
            Label = label,
            IsPrimary = isPrimary,
            IsDanger = isDanger,
        };
        button.Arguments["action_id"] = actionId;
        button.Arguments["actor_id"] = request.ActorId;
        button.Arguments["run_id"] = request.RunId;
        button.Arguments["step_id"] = request.StepId;
        if (approved.HasValue)
            button.Arguments["approved"] = approved.Value ? "true" : "false";
        return button;
    }

    private static ComposeContext BuildComposeContext() => new()
    {
        Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
    };

    private async Task<UserAgentCatalogEntry> ResolveTargetAsync(
        string deliveryTargetId,
        CancellationToken cancellationToken)
    {
        var target = await _agentRegistryQueryPort.GetAsync(deliveryTargetId, cancellationToken);
        if (target == null)
            throw new InvalidOperationException($"Agent delivery target not found: {deliveryTargetId}");

        if (!string.Equals(target.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported human interaction platform: {target.Platform}");

        return target;
    }

    private static bool ShouldSendApprovedContent(
        UserAgentCatalogEntry target,
        HumanApprovalResolution resolution) =>
        resolution.Approved &&
        !string.IsNullOrWhiteSpace(resolution.ResolvedContent) &&
        string.Equals(target.TemplateName, WorkflowAgentDefaults.TemplateName, StringComparison.OrdinalIgnoreCase);

    private async Task SendTextMessageAsync(
        UserAgentCatalogEntry target,
        string text,
        string emptyResponseMessage,
        string failurePrefix,
        CancellationToken cancellationToken) =>
        await SendMessageAsync(
            target,
            "text",
            JsonSerializer.Serialize(new { text }),
            emptyResponseMessage,
            failurePrefix,
            cancellationToken);

    private async Task SendInteractiveCardMessageAsync(
        UserAgentCatalogEntry target,
        string cardJson,
        string emptyResponseMessage,
        string failurePrefix,
        CancellationToken cancellationToken)
        => await SendMessageAsync(
            target,
            "interactive",
            cardJson,
            emptyResponseMessage,
            failurePrefix,
            cancellationToken);

    private async Task SendMessageAsync(
        UserAgentCatalogEntry target,
        string messageType,
        string contentJson,
        string emptyResponseMessage,
        string failurePrefix,
        CancellationToken cancellationToken)
    {
        var deliveryTarget = LarkConversationTargets.Resolve(
            target.LarkReceiveId,
            target.LarkReceiveIdType,
            target.ConversationId);
        if (deliveryTarget.FellBackToPrefixInference)
        {
            // Catalog entry predates the typed lark_receive_id fields; fall back to the prefix
            // heuristic on conversation_id and emit a breadcrumb so format drift is observable.
            _logger.LogDebug(
                "Feishu human interaction port resolved Lark receive target by prefix inference (legacy entry): agent={AgentId}, conversationId={ConversationId}, receiveIdType={ReceiveIdType}",
                target.AgentId,
                target.ConversationId,
                deliveryTarget.ReceiveIdType);
        }

        var outcome = await TrySendWithFallbackAsync(
            target,
            messageType,
            contentJson,
            deliveryTarget,
            emptyResponseMessage,
            cancellationToken);

        if (!outcome.Succeeded)
        {
            throw new InvalidOperationException(BuildLarkRejectionMessage(failurePrefix, outcome.LarkCode, outcome.Detail));
        }
    }

    private readonly record struct SendOutcome(bool Succeeded, int? LarkCode, string Detail)
    {
        public static SendOutcome Success() => new(true, null, string.Empty);
        public static SendOutcome Failed(int? larkCode, string detail) => new(false, larkCode, detail);
    }

    /// <summary>
    /// Mirrors <c>SkillRunnerGAgent.TrySendWithFallbackAsync</c>: tries the typed primary
    /// delivery target, then on a Lark <c>230002 bot not in chat</c> rejection retries once
    /// with the fallback target persisted on <see cref="UserAgentCatalogEntry.LarkReceiveIdFallback"/>.
    /// Returns success vs. failure (with Lark code+detail) so the caller can throw cleanly.
    /// </summary>
    private async Task<SendOutcome> TrySendWithFallbackAsync(
        UserAgentCatalogEntry target,
        string messageType,
        string contentJson,
        LarkReceiveTarget primary,
        string emptyResponseMessage,
        CancellationToken cancellationToken)
    {
        var primaryResult = await SendOutboundAsync(target, messageType, contentJson, primary, cancellationToken);
        if (string.IsNullOrWhiteSpace(primaryResult))
            throw new InvalidOperationException(emptyResponseMessage);
        if (!LarkProxyResponse.TryGetError(primaryResult, out var larkCode, out var detail))
            return SendOutcome.Success();

        if (larkCode != LarkBotErrorCodes.BotNotInChat)
            return SendOutcome.Failed(larkCode, detail);

        var fallbackId = target.LarkReceiveIdFallback?.Trim();
        var fallbackType = target.LarkReceiveIdTypeFallback?.Trim();
        if (string.IsNullOrEmpty(fallbackId) || string.IsNullOrEmpty(fallbackType))
            return SendOutcome.Failed(larkCode, detail);

        _logger.LogInformation(
            "Feishu human interaction port primary delivery target rejected as `bot not in chat` (code 230002); retrying with fallback typed pair: agent={AgentId}, fallbackType={FallbackType}",
            target.AgentId,
            fallbackType);

        var fallbackTarget = new LarkReceiveTarget(fallbackId, fallbackType, FellBackToPrefixInference: false);
        var fallbackResult = await SendOutboundAsync(target, messageType, contentJson, fallbackTarget, cancellationToken);
        if (string.IsNullOrWhiteSpace(fallbackResult))
            throw new InvalidOperationException(emptyResponseMessage);
        if (!LarkProxyResponse.TryGetError(fallbackResult, out var fallbackCode, out var fallbackDetail))
            return SendOutcome.Success();
        return SendOutcome.Failed(fallbackCode, fallbackDetail);
    }

    private async Task<string> SendOutboundAsync(
        UserAgentCatalogEntry target,
        string messageType,
        string contentJson,
        LarkReceiveTarget receiveTarget,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            receive_id = receiveTarget.ReceiveId,
            msg_type = messageType,
            content = contentJson,
        });

        return await _nyxIdApiClient.ProxyRequestAsync(
            target.NyxApiKey,
            target.NyxProviderSlug,
            $"open-apis/im/v1/messages?receive_id_type={receiveTarget.ReceiveIdType}",
            "POST",
            body,
            extraHeaders: null,
            cancellationToken);
    }

    private static string BuildLarkRejectionMessage(string failurePrefix, int? larkCode, string detail)
    {
        if (larkCode == LarkBotErrorCodes.OpenIdCrossApp)
        {
            // Mirrors the SkillRunnerGAgent recovery hint: the workflow agent's catalog target
            // was captured before union_id ingress existed and the persisted typed pair is
            // permanently relay-app-scoped. Surface the recreate-the-agent instruction inside
            // the exception message so it ends up in `/agent-status`'s `last_error` field
            // instead of the cryptic Lark `99992361 open_id cross app`.
            return
                $"{failurePrefix} (code={larkCode}): {detail}. " +
                "This workflow agent was created before cross-app union_id ingress existed; " +
                "delete and recreate it (`/agents` → Delete → `/social-media`) to pick up the cross-app safe target.";
        }

        if (larkCode == LarkBotErrorCodes.UserIdCrossTenant)
        {
            // Cross-tenant variant of the open_id case — even union_id fails. Same recovery
            // shape: recreate the agent so the chat_id-preferred outbound takes effect, or
            // align the NyxID `s/api-lark-bot` proxy with the channel-bot that received the
            // inbound event so the apps share a tenant.
            return
                $"{failurePrefix} (code={larkCode}): {detail}. " +
                "The outbound Lark app is in a different tenant than the inbound app, so " +
                "user-id translation is impossible. Delete and recreate the workflow agent " +
                "(`/agents` → Delete → `/social-media`) so the new chat_id-preferred outbound " +
                "path takes effect, or align the NyxID `s/api-lark-bot` proxy with the channel-bot " +
                "that received the inbound event.";
        }

        return larkCode is { } code
            ? $"{failurePrefix} (code={code}): {detail}"
            : $"{failurePrefix}: {detail}";
    }

    private static bool SupportsApproveReject(HumanInteractionRequest request) =>
        string.Equals(request.SuspensionType, "human_approval", StringComparison.OrdinalIgnoreCase) ||
        (request.Options.Contains("approve", StringComparer.OrdinalIgnoreCase) &&
         request.Options.Contains("reject", StringComparer.OrdinalIgnoreCase));

    private static string ResolveTitle(HumanInteractionRequest request) =>
        string.Equals(request.SuspensionType, "human_approval", StringComparison.OrdinalIgnoreCase)
            ? "Approval required."
            : "Input required.";

    private static string BuildApproveCommand(HumanInteractionRequest request, string? suffix = null) =>
        BuildCommand("/approve", request, suffix);

    private static string BuildRejectCommand(HumanInteractionRequest request, string? suffix = null) =>
        BuildCommand("/reject", request, suffix);

    private static string BuildSubmitCommand(HumanInteractionRequest request, string? suffix = null) =>
        BuildCommand("/submit", request, suffix);

    private static string BuildCommand(string verb, HumanInteractionRequest request, string? suffix)
    {
        var builder = new StringBuilder()
            .Append(verb)
            .Append(" actor_id=").Append(request.ActorId)
            .Append(" run_id=").Append(request.RunId)
            .Append(" step_id=").Append(request.StepId);

        var normalizedSuffix = NormalizeOptional(suffix);
        if (normalizedSuffix is not null)
            builder.Append(' ').Append(normalizedSuffix);

        return builder.ToString();
    }

    private static string BuildLegacyMarkdown(HumanInteractionRequest request)
    {
        var lines = new List<string> { $"**{request.Prompt}**" };
        if (!string.IsNullOrWhiteSpace(request.Content))
            lines.Add($"\n{request.Content!}");

        lines.Add($"\nRun: `{request.RunId}`");
        lines.Add($"Step: `{request.StepId}`");
        lines.Add($"Actor: `{request.ActorId}`");

        lines.Add("\nFallback commands if card actions are unavailable:");
        if (SupportsApproveReject(request))
        {
            lines.Add($"- Approve: `{BuildApproveCommand(request)}`");
            lines.Add($"- Approve with edits: `{BuildApproveCommand(request, "edited_content=\\\"final approved content\\\"")}`");
            lines.Add($"- Reject: `{BuildRejectCommand(request, "feedback=\\\"what should change\\\"")}`");
        }
        else
        {
            lines.Add($"- Submit: `{BuildSubmitCommand(request, "user_input=\\\"your response here\\\"")}`");
        }

        return string.Concat(lines);
    }


    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
