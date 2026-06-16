using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class FeishuCardNotificationPort : IChannelInteractionNotificationPort
{
    private readonly FeishuCardOutboundMessageSender _sender;
    private readonly LarkMessageComposer _composer;
    private readonly ILogger<FeishuCardNotificationPort> _logger;

    public FeishuCardNotificationPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        NyxIdApiClient nyxIdApiClient,
        LarkMessageComposer composer,
        ILogger<FeishuCardNotificationPort> logger,
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

    public async Task DeliverAsync(
        ChannelInteractionNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidatePayload(request);
        var target = await _sender.ResolveTargetAsync(
            request.DeliveryTargetId,
            "interaction notification",
            cancellationToken);

        await _sender.SendInteractiveCardMessageAsync(
            target,
            BuildCardJson(request, _composer),
            "Feishu interaction notification delivery returned empty response.",
            "Feishu interaction notification delivery failed",
            cancellationToken);

        _logger.LogInformation(
            "Delivered interaction notification card: target={DeliveryTargetId}, run={RunId}, step={StepId}",
            request.DeliveryTargetId,
            request.RunId,
            request.StepId);
    }

    internal static string BuildCardJson(ChannelInteractionNotificationRequest request) =>
        BuildCardJson(request, new LarkMessageComposer());

    internal static string BuildCardJson(
        ChannelInteractionNotificationRequest request,
        LarkMessageComposer composer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(composer);

        if (request.InteractionSpec is { } interactionSpec)
            return BuildInteractionCardJson(interactionSpec, composer);

        if (request.InteractionTemplateSpec is { } templateSpec)
            return BuildTemplateCardJson(templateSpec);

        throw new InvalidOperationException("Interaction notification payload is required.");
    }

    private static string BuildInteractionCardJson(
        InteractionSpec interactionSpec,
        LarkMessageComposer composer)
    {
        var content = InteractionSpecMapper.ToMessageContent(interactionSpec);
        var payload = composer.Compose(content, BuildComposeContext());
        if (!payload.IsInteractive)
            throw new InvalidOperationException("Interaction notification must render as an interactive Lark card.");

        return payload.ContentJson;
    }

    private static string BuildTemplateCardJson(InteractionTemplateSpec templateSpec)
    {
        if (string.IsNullOrWhiteSpace(templateSpec.TemplateId))
            throw new InvalidOperationException("Interaction template notification requires template_id.");

        return JsonSerializer.Serialize(new
        {
            type = "template",
            data = new
            {
                template_id = templateSpec.TemplateId,
                template_variable = templateSpec.TemplateVariable.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
            },
        });
    }

    private static ComposeContext BuildComposeContext() => new()
    {
        Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
    };

    private static void ValidatePayload(ChannelInteractionNotificationRequest request)
    {
        var payloadCount = 0;
        if (request.InteractionSpec is not null)
            payloadCount++;
        if (request.InteractionTemplateSpec is not null)
            payloadCount++;

        if (payloadCount != 1)
            throw new InvalidOperationException("Interaction notification requires exactly one typed payload.");
    }
}
