using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class ScheduledServiceInvocationDispatchPort : IScheduledServiceInvocationDispatchPort
{
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";

    private readonly IServiceInvocationPort _serviceInvocationPort;
    private readonly IScheduledServiceInvocationCredentialExchangePort _credentialExchangePort;

    public ScheduledServiceInvocationDispatchPort(
        IServiceInvocationPort serviceInvocationPort,
        IScheduledServiceInvocationCredentialExchangePort credentialExchangePort)
    {
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
        _credentialExchangePort = credentialExchangePort
            ?? throw new ArgumentNullException(nameof(credentialExchangePort));
    }

    public async Task<ScheduledServiceInvocationDispatchReceipt> DispatchAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(dispatch.Request);

        var request = await BuildInvocationRequestAsync(dispatch, ct);
        var receipt = await _serviceInvocationPort.InvokeAsync(request, ct);
        return new ScheduledServiceInvocationDispatchReceipt(
            true,
            receipt.CommandId ?? string.Empty,
            receipt.TargetActorId ?? string.Empty,
            receipt.CorrelationId ?? string.Empty);
    }

    private async Task<ServiceInvocationRequest> BuildInvocationRequestAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct)
    {
        if (dispatch.Auth?.SenderNyxId == null)
        {
            return EnrichChatPayload(
                dispatch.Request,
                dispatch.Headers,
                senderNyxIdAccessToken: null,
                projectSenderNyxIdAccessTokenToWorkflowCallerCredential:
                    dispatch.ProjectSenderNyxIdAccessTokenToWorkflowCallerCredential);
        }

        var exchange = await _credentialExchangePort.IssueSenderNyxIdAsync(dispatch.Auth.SenderNyxId, ct);
        if (!exchange.Succeeded)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(exchange.Error)
                ? "Scheduled service invocation sender NyxID credential exchange failed."
                : exchange.Error.Trim());
        }

        return EnrichChatPayload(
            dispatch.Request,
            dispatch.Headers,
            NormalizeSenderNyxIdAccessToken(exchange.AccessToken),
            dispatch.ProjectSenderNyxIdAccessTokenToWorkflowCallerCredential);
    }

    private static ServiceInvocationRequest EnrichChatPayload(
        ServiceInvocationRequest request,
        IReadOnlyDictionary<string, string>? headers,
        string? senderNyxIdAccessToken,
        bool projectSenderNyxIdAccessTokenToWorkflowCallerCredential)
    {
        if ((headers == null || headers.Count == 0) && string.IsNullOrWhiteSpace(senderNyxIdAccessToken))
            return request;

        var cloned = request.Clone();
        if (cloned.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return cloned;

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                if (string.Equals(key, LegacyConnectorHttpAuthorizationBlockedKey, StringComparison.Ordinal))
                    continue;

                chatRequest.Metadata[key] = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(senderNyxIdAccessToken))
        {
            var token = senderNyxIdAccessToken.Trim();
            var control = LLMControlContextMapper.FromPayload(chatRequest.LlmControl) with
            {
                SenderNyxIdAccessToken = token,
            };
            chatRequest.LlmControl = control.ToPayload();
            if (projectSenderNyxIdAccessTokenToWorkflowCallerCredential)
                chatRequest.ConnectorHttpAuthorization = $"Bearer {token}";
        }

        cloned.Payload = Any.Pack(chatRequest);
        return cloned;
    }

    private static string NormalizeSenderNyxIdAccessToken(string? accessToken)
    {
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(accessToken);
        if (parsed.IsMissing)
            throw new InvalidOperationException("Scheduled service invocation sender NyxID credential exchange returned an empty access token.");
        if (parsed.IsInvalid)
            throw new InvalidOperationException("Scheduled service invocation sender NyxID credential exchange returned an invalid access token.");

        return parsed.NormalizedBearerToken!;
    }
}
