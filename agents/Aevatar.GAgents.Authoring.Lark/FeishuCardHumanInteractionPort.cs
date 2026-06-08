using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

/// <summary>
/// Delivers workflow human-interaction suspensions and resolutions as Lark interactive cards
/// via the NyxID proxy. Card construction runs through <see cref="LarkMessageComposer"/> so the
/// outbound shape and Lark-native card schema stay owned by the composer; this port only knows
/// the <see cref="MessageContent"/> intent and the NyxID proxy transport (a proactive outbound
/// send to a user-scoped conversation, distinct from relay-reply traffic).
/// </summary>
public sealed class FeishuCardHumanInteractionPort : IHumanInteractionPort
{
    private readonly FeishuCardOutboundMessageSender _sender;
    private readonly LarkMessageComposer _composer;
    private readonly ILogger<FeishuCardHumanInteractionPort> _logger;

    public FeishuCardHumanInteractionPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        NyxIdApiClient nyxIdApiClient,
        LarkMessageComposer composer,
        ILogger<FeishuCardHumanInteractionPort> logger,
        ILarkOutboundDispatcher? larkOutboundDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(deliveryTargetReader);
        ArgumentNullException.ThrowIfNull(nyxIdApiClient);
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sender = new FeishuCardOutboundMessageSender(
            deliveryTargetReader,
            nyxIdApiClient,
            logger,
            larkOutboundDispatcher);
    }

    public async Task DeliverSuspensionAsync(
        HumanInteractionRequest request,
        string deliveryTargetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await _sender.ResolveTargetAsync(
            deliveryTargetId,
            "human interaction",
            cancellationToken);
        await _sender.SendInteractiveCardMessageAsync(
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

        var target = await _sender.ResolveTargetAsync(
            deliveryTargetId,
            "human interaction",
            cancellationToken);
        await _sender.SendTextMessageAsync(
            target,
            BuildApprovalResolutionText(resolution, target),
            "Feishu approval resolution delivery returned empty response.",
            "Feishu approval resolution delivery failed",
            cancellationToken);

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
        UserAgentDeliveryTarget? target = null)
    {
        var lines = new List<string>
        {
            resolution.Approved ? "Approval recorded." : "Rejection recorded.",
            $"Run ID: {resolution.RunId}",
            $"Step ID: {resolution.StepId}",
        };

        if (!string.IsNullOrWhiteSpace(resolution.Feedback))
            lines.Add($"Feedback: {resolution.Feedback}");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Builds the Lark interactive card JSON for the suspension request by projecting it onto a
    /// <see cref="MessageContent"/> intent and delegating rendering to <see cref="LarkMessageComposer"/>.
    /// </summary>
    /// <remarks>
    /// The outbound button-value payload stays JSON-compatible with the inbound card-action parser
    /// in <see cref="Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayTransport"/> at the Lark/Nyx
    /// boundary, while repository-owned workflow resume semantics are authored as
    /// <see cref="WorkflowResumeActionPayload"/>.
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
        if (request.InteractionSpec is not null)
            return BuildTypedSuspensionIntent(request);

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

    private static MessageContent BuildTypedSuspensionIntent(HumanInteractionRequest request)
    {
        var intent = InteractionSpecMapper.ToMessageContent(request.InteractionSpec!);
        var actions = EnumerateActions(intent).ToArray();
        foreach (var action in actions)
        {
            if (action.Kind != ActionElementKind.FormSubmit)
                continue;

            action.WorkflowResume = BuildWorkflowResumePayload(request, ResolveTypedApprovalDecision(action));
        }

        return intent;
    }

    private static IEnumerable<ActionElement> EnumerateActions(MessageContent intent)
    {
        foreach (var action in intent.Actions)
            yield return action;
        foreach (var card in intent.Cards)
        {
            foreach (var action in card.Actions)
                yield return action;
        }
    }

    private static bool? ResolveTypedApprovalDecision(ActionElement action) =>
        action.WorkflowResume?.HasApproved == true
            ? action.WorkflowResume.Approved
            : null;

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
            WorkflowResume = BuildWorkflowResumePayload(request, approved),
        };
        button.Arguments["action_id"] = actionId;
        return button;
    }

    private static WorkflowResumeActionPayload BuildWorkflowResumePayload(
        HumanInteractionRequest request,
        bool? approved)
    {
        var payload = new WorkflowResumeActionPayload
        {
            ActorId = request.ActorId,
            RunId = request.RunId,
            StepId = request.StepId,
        };
        if (approved.HasValue)
            payload.Approved = approved.Value;
        return payload;
    }

    private static ComposeContext BuildComposeContext() => new()
    {
        Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
    };

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
