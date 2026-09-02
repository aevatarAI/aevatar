using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Integration.AI;

public sealed class HumanInteractionChannelToolSource : IAgentToolSource
{
    private readonly IChannelInteractionNotificationPort _notificationPort;
    private readonly ILogger _logger;

    public HumanInteractionChannelToolSource(
        IChannelInteractionNotificationPort notificationPort,
        ILogger<HumanInteractionChannelToolSource>? logger = null)
    {
        _notificationPort = notificationPort;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HumanInteractionChannelToolSource>.Instance;
    }

    public const string DeliveryCapability = "human_interaction.delivery";
    public const string ResolutionCapability = "human_interaction.resolution_update";

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>(
        [
            new DeliveryTool(_notificationPort, _logger),
            new ResolutionTool(_notificationPort, _logger),
        ]);

    private sealed class DeliveryTool(
        IChannelInteractionNotificationPort notificationPort,
        ILogger logger) : IAgentTool, IAgentToolCapabilityDescriptor
    {
        public string Name => "human_interaction_channel_delivery";

        public string Description =>
            "Delivers workflow human-interaction suspensions through the configured channel interaction notification port. Capability: human_interaction.delivery.";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public IReadOnlyCollection<string> Capabilities { get; } = [DeliveryCapability];

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var envelope = JsonSerializer.Deserialize<HumanInteractionDeliveryEnvelope>(argumentsJson, JsonOptions) ??
                           throw new InvalidOperationException("Human interaction delivery payload is required.");
            var interaction = envelope.Interaction ??
                              throw new InvalidOperationException("Human interaction payload is required.");

            var request = new ChannelInteractionNotificationRequest
            {
                ActorId = interaction.ActorId,
                RunId = interaction.RunId,
                StepId = interaction.StepId,
                DeliveryTargetId = envelope.DeliveryTargetId ?? string.Empty,
                InteractionSpec = interaction.InteractionSpec?.Clone() ?? BuildInteractionSpec(interaction),
            };

            logger.LogInformation(
                "Delivering human interaction through channel notification port: actor={ActorId}, run={RunId}, step={StepId}, deliveryTargetId={DeliveryTargetId}, title={Title}, actions={ActionCount}",
                request.ActorId,
                request.RunId,
                request.StepId,
                request.DeliveryTargetId,
                request.InteractionSpec.Title,
                request.InteractionSpec.Actions.Count);

            await notificationPort.DeliverAsync(request, ct);

            logger.LogInformation(
                "Delivered human interaction through channel notification port: actor={ActorId}, run={RunId}, step={StepId}, deliveryTargetId={DeliveryTargetId}",
                request.ActorId,
                request.RunId,
                request.StepId,
                request.DeliveryTargetId);

            return SuccessJson;
        }
    }

    private sealed class ResolutionTool(
        IChannelInteractionNotificationPort notificationPort,
        ILogger logger) : IAgentTool, IAgentToolCapabilityDescriptor
    {
        public string Name => "human_interaction_channel_resolution_update";

        public string Description =>
            "Delivers workflow human-approval resolution updates through the configured channel interaction notification port. Capability: human_interaction.resolution_update.";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public IReadOnlyCollection<string> Capabilities { get; } = [ResolutionCapability];

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            var envelope = JsonSerializer.Deserialize<HumanApprovalResolutionDeliveryEnvelope>(argumentsJson, JsonOptions) ??
                           throw new InvalidOperationException("Human approval resolution payload is required.");
            var resolution = envelope.Resolution ??
                             throw new InvalidOperationException("Human approval resolution is required.");

            var request = new ChannelInteractionNotificationRequest
            {
                ActorId = resolution.ActorId,
                RunId = resolution.RunId,
                StepId = resolution.StepId,
                DeliveryTargetId = envelope.DeliveryTargetId ?? string.Empty,
                InteractionSpec = BuildResolutionSpec(resolution),
            };

            logger.LogInformation(
                "Delivering human approval resolution through channel notification port: actor={ActorId}, run={RunId}, step={StepId}, deliveryTargetId={DeliveryTargetId}, approved={Approved}, timedOut={TimedOut}",
                request.ActorId,
                request.RunId,
                request.StepId,
                request.DeliveryTargetId,
                resolution.Approved,
                resolution.TimedOut);

            await notificationPort.DeliverAsync(request, ct);

            logger.LogInformation(
                "Delivered human approval resolution through channel notification port: actor={ActorId}, run={RunId}, step={StepId}, deliveryTargetId={DeliveryTargetId}",
                request.ActorId,
                request.RunId,
                request.StepId,
                request.DeliveryTargetId);

            return SuccessJson;
        }
    }

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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string SuccessJson = "{\"success\":true}";

    private sealed record HumanInteractionDeliveryEnvelope
    {
        public string? DeliveryTargetId { get; init; }

        public HumanInteractionRequest? Interaction { get; init; }
    }

    private sealed record HumanApprovalResolutionDeliveryEnvelope
    {
        public string? DeliveryTargetId { get; init; }

        public HumanApprovalResolution? Resolution { get; init; }
    }
}
