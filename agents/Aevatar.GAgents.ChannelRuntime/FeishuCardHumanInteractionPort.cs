using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class FeishuCardHumanInteractionPort : IHumanInteractionPort
{
    private readonly IUserAgentCatalogRuntimeQueryPort _agentRegistryQueryPort;
    private readonly NyxIdApiClient _nyxIdApiClient;
    private readonly ILogger<FeishuCardHumanInteractionPort> _logger;

    public FeishuCardHumanInteractionPort(
        IUserAgentCatalogRuntimeQueryPort agentRegistryQueryPort,
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
        await SendTextMessageAsync(
            target,
            BuildSuspensionText(request),
            "Feishu human interaction delivery returned empty response.",
            "Feishu human interaction delivery failed",
            cancellationToken);

        _logger.LogInformation(
            "Delivered human interaction instructions: target={DeliveryTargetId}, run={RunId}, step={StepId}",
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

    internal static string BuildCardJson(HumanInteractionRequest request)
    {
        var elements = new List<object>
        {
            new
            {
                tag = "markdown",
                content = BuildLegacyMarkdown(request),
            },
        };

        if (SupportsApproveReject(request))
        {
            elements.Add(new
            {
                tag = "form",
                name = "human_interaction_form",
                elements = new object[]
                {
                    BuildLegacyInput("edited_content", "Edited Draft (Optional)", "Paste the final draft here before approving"),
                    BuildLegacyInput("user_input", "Rejection Feedback (Optional)", "Explain what should change if you reject"),
                    BuildLegacyActionButton("Approve", "primary", request, approved: true),
                    BuildLegacyActionButton("Reject", "default", request, approved: false),
                },
            });
        }

        return JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new { wide_screen_mode = true },
            header = new
            {
                title = new
                {
                    tag = "plain_text",
                    content = ResolveTitle(request),
                },
                template = SupportsApproveReject(request) ? "orange" : "blue",
            },
            body = new { elements },
        });
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

    private static object BuildLegacyActionButton(string label, string style, HumanInteractionRequest request, bool approved) =>
        new
        {
            tag = "button",
            type = style,
            name = approved ? "approve" : "reject",
            form_action_type = "submit",
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

    private static object BuildLegacyInput(string name, string label, string placeholder) =>
        new
        {
            tag = "input",
            name,
            label = new
            {
                tag = "plain_text",
                content = label,
            },
            placeholder = new
            {
                tag = "plain_text",
                content = placeholder,
            },
        };

    private static string BuildLegacyMarkdown(HumanInteractionRequest request)
    {
        var lines = new List<string> { $"**{request.Prompt}**" };
        if (!string.IsNullOrWhiteSpace(request.Content))
            lines.Add($"\n{request.Content!}");

        lines.Add($"\nRun: `{request.RunId}`");
        lines.Add($"Step: `{request.StepId}`");
        return string.Concat(lines);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
