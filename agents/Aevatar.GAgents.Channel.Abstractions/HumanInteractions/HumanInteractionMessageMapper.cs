using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Converts workflow-owned human interaction notifications into channel-neutral message content.
/// </summary>
public static class HumanInteractionMessageMapper
{
    /// <summary>
    /// Maps one typed human interaction notification request into composer-ready channel message content.
    /// </summary>
    public static MessageContent ToMessageContent(ChannelInteractionNotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidatePayload(request);

        if (request.InteractionSpec is { } interactionSpec)
            return InteractionSpecMapper.ToMessageContent(interactionSpec, BuildWorkflowResumePayload(request));

        return ToTemplateFallbackMessage(request.InteractionTemplateSpec!);
    }

    private static WorkflowResumeActionPayload BuildWorkflowResumePayload(ChannelInteractionNotificationRequest request) =>
        new()
        {
            ActorId = request.ActorId,
            RunId = request.RunId,
            StepId = request.StepId,
        };

    private static MessageContent ToTemplateFallbackMessage(InteractionTemplateSpec templateSpec)
    {
        if (string.IsNullOrWhiteSpace(templateSpec.TemplateId))
            throw new InvalidOperationException("Interaction template notification requires template_id.");

        var content = new MessageContent
        {
            Text = $"Interaction notification template: {templateSpec.TemplateId}",
        };

        var card = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "Interaction notification",
            Text = $"Template ID: {templateSpec.TemplateId}",
        };

        foreach (var variable in templateSpec.TemplateVariable.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            card.Fields.Add(new CardField
            {
                Title = variable.Key,
                Text = variable.Value,
                IsShort = false,
            });
        }

        content.Cards.Add(card);
        return content;
    }

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
