using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class FeishuCardHumanInteractionPort : IHumanInteractionPort
{
    private const string AgentBuilderListAgentsAction = "list_agents";
    private const string AgentBuilderRunAgentAction = "run_agent";

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
            BuildApprovalResolutionCardJson(resolution, target),
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
            elements.Add(BuildEditedContentInput());
            elements.Add(BuildFeedbackInput());
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

    internal static string BuildApprovalResolutionCardJson(
        HumanApprovalResolution resolution,
        AgentRegistryEntry? target = null)
    {
        var lines = new List<string>
        {
            resolution.Approved
                ? "**Approval recorded.** The approved content will be posted below."
                : "**Rejection recorded.** The workflow will follow the rejection path.",
            $"\nRun: `{EscapeMarkdown(resolution.RunId)}`",
            $"Step: `{EscapeMarkdown(resolution.StepId)}`",
        };

        if (!string.IsNullOrWhiteSpace(resolution.Feedback))
            lines.Add($"\nFeedback: {EscapeMarkdown(resolution.Feedback!)}");

        var elements = new List<object>
        {
            new
            {
                tag = "markdown",
                content = string.Concat(lines),
            },
        };

        if (ShouldOfferRerun(target, resolution))
        {
            elements.Add(new
            {
                tag = "action",
                actions = new object[]
                {
                    BuildAgentActionButton(
                        "Run Again",
                        "primary",
                        AgentBuilderRunAgentAction,
                        target!.AgentId,
                        resolution.Feedback),
                    BuildAgentActionButton(
                        "View Agents",
                        "default",
                        AgentBuilderListAgentsAction,
                        target.AgentId,
                        null),
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
                    content = resolution.Approved ? "Approval Recorded" : "Rejection Recorded",
                },
                template = resolution.Approved ? "green" : "red",
            },
            elements,
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

    private static bool ShouldSendApprovedContent(
        AgentRegistryEntry target,
        HumanApprovalResolution resolution) =>
        resolution.Approved &&
        !string.IsNullOrWhiteSpace(resolution.ResolvedContent) &&
        string.Equals(target.TemplateName, WorkflowAgentDefaults.TemplateName, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldOfferRerun(
        AgentRegistryEntry? target,
        HumanApprovalResolution resolution) =>
        target is not null &&
        !resolution.Approved &&
        string.Equals(target.TemplateName, WorkflowAgentDefaults.TemplateName, StringComparison.OrdinalIgnoreCase);

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

    private async Task SendTextMessageAsync(
        AgentRegistryEntry target,
        string text,
        string emptyResponseMessage,
        string failurePrefix,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            receive_id = target.ConversationId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text }),
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

    private static object BuildAgentActionButton(
        string label,
        string style,
        string action,
        string agentId,
        string? revisionFeedback) =>
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
                agent_builder_action = action,
                agent_id = agentId,
                revision_feedback = NormalizeOptional(revisionFeedback) ?? string.Empty,
            },
        };

    private static object BuildEditedContentInput() =>
        new
        {
            tag = "input",
            name = "edited_content",
            label = new
            {
                tag = "plain_text",
                content = "Edited Draft (Optional)",
            },
            placeholder = new
            {
                tag = "plain_text",
                content = "Paste the final draft here before approving",
            },
        };

    private static object BuildFeedbackInput() =>
        new
        {
            tag = "input",
            name = "user_input",
            label = new
            {
                tag = "plain_text",
                content = "Rejection Feedback (Optional)",
            },
            placeholder = new
            {
                tag = "plain_text",
                content = "Explain what should change if you reject",
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

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
