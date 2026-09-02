using System.Text.Json;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

public static class LarkInteractionCardRenderer
{
    public static string BuildCardJson(ChannelInteractionNotificationRequest request) =>
        BuildCardJson(request, new LarkMessageComposer());

    public static string BuildCardJson(
        ChannelInteractionNotificationRequest request,
        LarkMessageComposer composer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(composer);

        ValidatePayload(request);

        if (request.InteractionSpec is { } interactionSpec)
            return BuildInteractionCardJson(interactionSpec, composer, BuildWorkflowResumePayload(request));

        if (request.InteractionTemplateSpec is { } templateSpec)
            return BuildTemplateCardJson(templateSpec);

        throw new InvalidOperationException("Interaction notification payload is required.");
    }

    private static string BuildInteractionCardJson(
        InteractionSpec interactionSpec,
        LarkMessageComposer composer,
        WorkflowResumeActionPayload workflowResume)
    {
        var content = InteractionSpecMapper.ToMessageContent(interactionSpec, workflowResume);
        var payload = composer.Compose(content, BuildComposeContext());
        if (!payload.IsInteractive)
            throw new InvalidOperationException("Interaction notification must render as an interactive Lark card.");

        return payload.ContentJson;
    }

    private static WorkflowResumeActionPayload BuildWorkflowResumePayload(ChannelInteractionNotificationRequest request) =>
        new()
        {
            ActorId = request.ActorId,
            RunId = request.RunId,
            StepId = request.StepId,
        };

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
