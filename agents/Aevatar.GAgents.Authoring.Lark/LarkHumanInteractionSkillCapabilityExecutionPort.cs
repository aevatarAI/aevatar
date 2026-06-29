using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class LarkHumanInteractionSkillCapabilityExecutionPort : ISkillCapabilityExecutionPort
{
    public const string DeliveryCapability = "human_interaction.delivery";
    public const string ResolutionCapability = "human_interaction.resolution_update";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FeishuCardOutboundMessageSender _sender;
    private readonly LarkMessageComposer _composer;
    private readonly ILogger<LarkHumanInteractionSkillCapabilityExecutionPort> _logger;

    public LarkHumanInteractionSkillCapabilityExecutionPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        NyxIdApiClient nyxIdApiClient,
        LarkMessageComposer composer,
        ILogger<LarkHumanInteractionSkillCapabilityExecutionPort> logger,
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

    public async Task<string> ExecuteAsync(
        SkillCapabilityExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Capability.Capability switch
        {
            DeliveryCapability => await DeliverInteractionAsync(request.ArgumentsJson, ct),
            ResolutionCapability => await DeliverResolutionAsync(request.ArgumentsJson, ct),
            _ => JsonSerializer.Serialize(new
            {
                success = false,
                error = $"Unsupported Lark human interaction capability '{request.Capability.Capability}'.",
            }, JsonOptions),
        };
    }

    private async Task<string> DeliverInteractionAsync(string argumentsJson, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<HumanInteractionDeliveryEnvelope>(argumentsJson, JsonOptions) ??
                       throw new InvalidOperationException("Human interaction delivery payload is required.");
        var interaction = envelope.Interaction ??
                          throw new InvalidOperationException("Human interaction payload is required.");
        var notification = new ChannelInteractionNotificationRequest
        {
            ActorId = interaction.ActorId,
            RunId = interaction.RunId,
            StepId = interaction.StepId,
            DeliveryTargetId = envelope.DeliveryTargetId ?? string.Empty,
            InteractionSpec = interaction.InteractionSpec?.Clone() ?? BuildInteractionSpec(interaction),
        };

        await SendCardAsync(notification, ct);
        return JsonSerializer.Serialize(new { success = true }, JsonOptions);
    }

    private async Task<string> DeliverResolutionAsync(string argumentsJson, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<HumanApprovalResolutionDeliveryEnvelope>(argumentsJson, JsonOptions) ??
                       throw new InvalidOperationException("Human approval resolution payload is required.");
        var resolution = envelope.Resolution ??
                         throw new InvalidOperationException("Human approval resolution is required.");
        var notification = new ChannelInteractionNotificationRequest
        {
            ActorId = resolution.ActorId,
            RunId = resolution.RunId,
            StepId = resolution.StepId,
            DeliveryTargetId = envelope.DeliveryTargetId ?? string.Empty,
            InteractionSpec = BuildResolutionSpec(resolution),
        };

        await SendCardAsync(notification, ct);
        return JsonSerializer.Serialize(new { success = true }, JsonOptions);
    }

    private async Task SendCardAsync(
        ChannelInteractionNotificationRequest notification,
        CancellationToken ct)
    {
        var target = await _sender.ResolveTargetAsync(
            notification.DeliveryTargetId,
            "Lark human interaction skill capability",
            ct);

        await _sender.SendInteractiveCardMessageAsync(
            target,
            BuildCardJson(notification),
            "Lark human interaction skill delivery returned empty response.",
            "Lark human interaction skill delivery failed",
            ct);

        _logger.LogInformation(
            "Delivered Lark human interaction skill card: target={DeliveryTargetId}, run={RunId}, step={StepId}",
            notification.DeliveryTargetId,
            notification.RunId,
            notification.StepId);
    }

    internal string BuildCardJson(ChannelInteractionNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var interactionSpec = request.InteractionSpec ??
                              throw new InvalidOperationException("Lark human interaction skill requires an interaction spec.");
        var content = InteractionSpecMapper.ToMessageContent(interactionSpec, BuildWorkflowResumePayload(request));
        var payload = _composer.Compose(content, BuildComposeContext());
        if (!payload.IsInteractive)
            throw new InvalidOperationException("Lark human interaction skill must render an interactive card.");

        return payload.ContentJson;
    }

    private static WorkflowResumeActionPayload BuildWorkflowResumePayload(ChannelInteractionNotificationRequest request) =>
        new()
        {
            ActorId = request.ActorId,
            RunId = request.RunId,
            StepId = request.StepId,
        };

    private static InteractionSpec BuildInteractionSpec(HumanInteractionRequest request)
    {
        var spec = new InteractionSpec
        {
            Title = IsApproval(request) ? "Approval required." : "Input required.",
            Body = BuildBody(request),
            Disposition = InteractionDisposition.Normal,
        };

        if (IsApproval(request))
        {
            spec.Actions.Add(BuildTextInput("edited_content", "Edited Draft (Optional)", "Paste the final draft here before approving"));
            spec.Actions.Add(BuildTextInput("user_input", "Rejection Feedback (Optional)", "Explain what should change if you reject"));
            spec.Actions.Add(BuildSubmitAction("approve", "Approve", InteractionActionStyle.Primary, InteractionApprovalDecision.Approve));
            spec.Actions.Add(BuildSubmitAction("reject", "Reject", InteractionActionStyle.Danger, InteractionApprovalDecision.Reject));
        }
        else
        {
            spec.Actions.Add(BuildTextInput("user_input", "Response", "Enter your response"));
            spec.Actions.Add(BuildSubmitAction("submit", "Submit", InteractionActionStyle.Primary, InteractionApprovalDecision.Unspecified));
        }

        return spec;
    }

    private static InteractionSpec BuildResolutionSpec(HumanApprovalResolution resolution)
    {
        var status = resolution.Approved ? "Approval recorded." : "Rejection recorded.";
        var body = string.IsNullOrWhiteSpace(resolution.Feedback)
            ? $"Run ID: {resolution.RunId}\nStep ID: {resolution.StepId}"
            : $"Run ID: {resolution.RunId}\nStep ID: {resolution.StepId}\nFeedback: {resolution.Feedback}";

        var spec = new InteractionSpec
        {
            Title = status,
            Body = body,
            Disposition = InteractionDisposition.Normal,
        };
        spec.Actions.Add(new InteractionAction
        {
            Kind = InteractionActionKind.Button,
            ActionId = "resolved",
            Label = resolution.TimedOut ? "Timed out" : "Resolved",
            Disabled = true,
        });
        return spec;
    }

    private static string BuildBody(HumanInteractionRequest request)
    {
        var lines = new List<string>
        {
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

        return string.Join('\n', lines);
    }

    private static InteractionAction BuildTextInput(string actionId, string label, string placeholder) =>
        new()
        {
            Kind = InteractionActionKind.TextInput,
            ActionId = actionId,
            Label = label,
            Placeholder = placeholder,
        };

    private static InteractionAction BuildSubmitAction(
        string actionId,
        string label,
        InteractionActionStyle style,
        InteractionApprovalDecision decision) =>
        new()
        {
            Kind = InteractionActionKind.FormSubmit,
            ActionId = actionId,
            Label = label,
            Style = style,
            ApprovalDecision = decision,
        };

    private static bool IsApproval(HumanInteractionRequest request) =>
        string.Equals(request.SuspensionType, "human_approval", StringComparison.OrdinalIgnoreCase) ||
        (request.Options.Contains("approve", StringComparer.OrdinalIgnoreCase) &&
         request.Options.Contains("reject", StringComparer.OrdinalIgnoreCase));

    private static ComposeContext BuildComposeContext() => new()
    {
        Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
    };

    private sealed record HumanInteractionDeliveryEnvelope
    {
        public string? DeliveryTargetId { get; init; }

        public string? Capability { get; init; }

        public HumanInteractionRequest? Interaction { get; init; }
    }

    private sealed record HumanApprovalResolutionDeliveryEnvelope
    {
        public string? DeliveryTargetId { get; init; }

        public string? Capability { get; init; }

        public HumanApprovalResolution? Resolution { get; init; }
    }
}
