using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

internal static class ScheduledChatPromptTemplateRenderer
{
    public static ServiceInvocationRequest Render(
        ServiceInvocationRequest request,
        ScheduledDispatchFireContext? fireContext)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (fireContext == null ||
            request.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true ||
            string.IsNullOrEmpty(chatRequest.Prompt))
        {
            return request;
        }

        var renderedPrompt = ScheduledDispatchPromptTemplate.Render(
            chatRequest.Prompt,
            fireContext);
        if (string.Equals(renderedPrompt, chatRequest.Prompt, StringComparison.Ordinal))
            return request;

        var renderedRequest = request.Clone();
        chatRequest.Prompt = renderedPrompt;
        renderedRequest.Payload = Any.Pack(chatRequest);
        return renderedRequest;
    }
}
