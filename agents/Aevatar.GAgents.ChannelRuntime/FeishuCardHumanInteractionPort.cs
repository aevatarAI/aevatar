using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class FeishuCardHumanInteractionPort : IHumanInteractionPort
{
    private readonly IAgentRegistryQueryPort _agentRegistryQueryPort;
    private readonly NyxIdApiClient _nyxIdApiClient;
    private readonly ILogger<FeishuCardHumanInteractionPort> _logger;

    public FeishuCardHumanInteractionPort(
        IAgentRegistryQueryPort agentRegistryQueryPort,
        NyxIdApiClient nyxIdApiClient,
        ILogger<FeishuCardHumanInteractionPort> logger)
    {
        _agentRegistryQueryPort = agentRegistryQueryPort ?? throw new ArgumentNullException(nameof(agentRegistryQueryPort));
        _nyxIdApiClient = nyxIdApiClient ?? throw new ArgumentNullException(nameof(nyxIdApiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task DeliverSuspensionAsync(
        HumanInteractionRequest request,
        string deliveryTargetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await ResolveTargetAsync(deliveryTargetId, cancellationToken);
        await SendInteractiveCardAsync(
            target,
            BuildCardJson(request),
            "Feishu card delivery returned empty response.",
            "Feishu card delivery failed",
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
        await SendInteractiveCardAsync(
            target,
            BuildApprovalResolutionCardJson(resolution),
            "Feishu approval resolution delivery returned empty response.",
            "Feishu approval resolution delivery failed",
            cancellationToken);

        _logger.LogInformation(
            "Delivered human approval resolution card: target={DeliveryTargetId}, run={RunId}, step={StepId}, approved={Approved}",
            deliveryTargetId,
            resolution.RunId,
            resolution.StepId,
            resolution.Approved);
    }

    internal static string BuildCardJson(HumanInteractionRequest request)
    {
        var elements = new List<object>
        {
            new
            {
                tag = "markdown",
                content = BuildMarkdown(request),
            },
        };

        if (SupportsApproveReject(request))
        {
            elements.Add(new
            {
                tag = "action",
                actions = new object[]
                {
                    BuildActionButton("Approve", "primary", request, approved: true),
                    BuildActionButton("Reject", "default", request, approved: false),
                },
            });
        }

        return JsonSerializer.Serialize(new
        {
            config = new
            {
                wide_screen_mode = true,
            },
            header = new
            {
                title = new
                {
                    tag = "plain_text",
                    content = ResolveTitle(request),
                },
                template = SupportsApproveReject(request) ? "orange" : "blue",
            },
            elements,
        });
    }

    internal static string BuildApprovalResolutionCardJson(HumanApprovalResolution resolution)
    {
        var lines = new List<string>
        {
            resolution.Approved
                ? "**Approval recorded.** The workflow will continue."
                : "**Rejection recorded.** The workflow will follow the rejection path.",
            $"\nRun: `{EscapeMarkdown(resolution.RunId)}`",
            $"Step: `{EscapeMarkdown(resolution.StepId)}`",
        };

        if (!string.IsNullOrWhiteSpace(resolution.UserInput))
            lines.Add($"\nFeedback: {EscapeMarkdown(resolution.UserInput!)}");

        return JsonSerializer.Serialize(new
        {
            config = new
            {
                wide_screen_mode = true,
            },
            header = new
            {
                title = new
                {
                    tag = "plain_text",
                    content = resolution.Approved ? "Approval Recorded" : "Rejection Recorded",
                },
                template = resolution.Approved ? "green" : "red",
            },
            elements = new object[]
            {
                new
                {
                    tag = "markdown",
                    content = string.Concat(lines),
                },
            },
        });
    }

    private async Task<AgentRegistryEntry> ResolveTargetAsync(
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

    private async Task SendInteractiveCardAsync(
        AgentRegistryEntry target,
        string cardJson,
        string emptyResponseMessage,
        string failurePrefix,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            receive_id = target.ConversationId,
            msg_type = "interactive",
            content = cardJson,
        });

        var result = await _nyxIdApiClient.ProxyRequestAsync(
            target.NyxApiKey,
            target.NyxProviderSlug,
            "open-apis/im/v1/messages?receive_id_type=chat_id",
            "POST",
            body,
            extraHeaders: null,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException(emptyResponseMessage);

        if (result.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{failurePrefix}: {result}");
    }

    private static object BuildActionButton(string label, string style, HumanInteractionRequest request, bool approved) =>
        new
        {
            tag = "button",
            type = style,
            text = new
            {
                tag = "plain_text",
                content = label,
            },
            value = new
            {
                actor_id = request.ActorId,
                run_id = request.RunId,
                step_id = request.StepId,
                approved,
            },
        };

    private static bool SupportsApproveReject(HumanInteractionRequest request) =>
        string.Equals(request.SuspensionType, "human_approval", StringComparison.OrdinalIgnoreCase) ||
        (request.Options.Contains("approve", StringComparer.OrdinalIgnoreCase) &&
         request.Options.Contains("reject", StringComparer.OrdinalIgnoreCase));

    private static string ResolveTitle(HumanInteractionRequest request) =>
        string.Equals(request.SuspensionType, "human_approval", StringComparison.OrdinalIgnoreCase)
            ? "Approval Required"
            : "Input Required";

    private static string BuildMarkdown(HumanInteractionRequest request)
    {
        var lines = new List<string>
        {
            $"**{EscapeMarkdown(request.Prompt)}**",
        };

        if (!string.IsNullOrWhiteSpace(request.Content))
            lines.Add($"\n{EscapeMarkdown(request.Content!)}");

        lines.Add($"\nRun: `{request.RunId}`");
        lines.Add($"Step: `{request.StepId}`");
        return string.Concat(lines);
    }

    private static string EscapeMarkdown(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
}
