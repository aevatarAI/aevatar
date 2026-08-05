using Aevatar.AI.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Schedules;

public static class ScheduledServiceInvocationPayloadPolicy
{
    public const string ConnectorHttpAuthorizationKey = "connector.http.authorization";

    public static bool IsConnectorHttpAuthorizationKey(string? key) =>
        string.Equals(key?.Trim(), ConnectorHttpAuthorizationKey, StringComparison.OrdinalIgnoreCase);

    public static Any StripScheduleOwnedCredentialFields(Any payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Is(ChatRequestEvent.Descriptor))
        {
            var chatRequest = payload.Unpack<ChatRequestEvent>();
            StripScheduleOwnedCredentialFields(chatRequest);
            return Any.Pack(chatRequest);
        }

        if (payload.Is(ServiceInvocationRequest.Descriptor))
        {
            return Any.Pack(StripScheduleOwnedCredentialFields(payload.Unpack<ServiceInvocationRequest>()));
        }

        return payload.Clone();
    }

    public static ServiceInvocationRequest StripScheduleOwnedCredentialFields(ServiceInvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cloned = request.Clone();
        if (cloned.Payload != null)
            cloned.Payload = StripScheduleOwnedCredentialFields(cloned.Payload);

        return cloned;
    }

    private static void StripScheduleOwnedCredentialFields(ChatRequestEvent chatRequest)
    {
        chatRequest.ConnectorHttpAuthorization = string.Empty;
        chatRequest.CallerSourceReadableNyxIdBearerToken = string.Empty;
        chatRequest.CallerDurableCredential = null;
        RemoveConnectorHttpAuthorizationKeys(chatRequest.Metadata);
        RemoveConnectorHttpAuthorizationKeys(chatRequest.Headers);

        if (chatRequest.LlmControl != null)
        {
            chatRequest.LlmControl.NyxIdAccessToken = string.Empty;
            chatRequest.LlmControl.NyxIdOrgToken = string.Empty;
            chatRequest.LlmControl.SenderNyxIdAccessToken = string.Empty;
        }

        if (chatRequest.ToolContext?.Credentials != null)
        {
            chatRequest.ToolContext.Credentials.NyxIdAccessToken = string.Empty;
            chatRequest.ToolContext.Credentials.NyxIdOrgToken = string.Empty;
            chatRequest.ToolContext.Credentials.SenderNyxIdAccessToken = string.Empty;
            chatRequest.ToolContext.Credentials.SourceReadableNyxIdAccessToken = string.Empty;
        }
    }

    private static void RemoveConnectorHttpAuthorizationKeys(IDictionary<string, string> values)
    {
        foreach (var key in values.Keys.Where(IsConnectorHttpAuthorizationKey).ToArray())
            values.Remove(key);
    }
}
