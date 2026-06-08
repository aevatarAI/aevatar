using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Schedules;

public sealed class ScheduledServiceInvocationDispatchPort : IScheduledServiceInvocationDispatchPort
{
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
            return EnrichChatPayload(dispatch.Request, dispatch.Headers, senderNyxIdAccessToken: null);

        var exchange = await _credentialExchangePort.IssueSenderNyxIdAsync(dispatch.Auth.SenderNyxId, ct);
        if (!exchange.Succeeded || string.IsNullOrWhiteSpace(exchange.AccessToken))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(exchange.Error)
                ? "Scheduled service invocation sender NyxID credential exchange failed."
                : exchange.Error.Trim());
        }

        return EnrichChatPayload(dispatch.Request, dispatch.Headers, exchange.AccessToken);
    }

    private static ServiceInvocationRequest EnrichChatPayload(
        ServiceInvocationRequest request,
        IReadOnlyDictionary<string, string>? headers,
        string? senderNyxIdAccessToken)
    {
        if ((headers == null || headers.Count == 0) && string.IsNullOrWhiteSpace(senderNyxIdAccessToken))
            return request;

        var cloned = request.Clone();
        if (cloned.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return cloned;

        if (headers != null)
        {
            foreach (var (key, value) in headers)
                chatRequest.Metadata[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(senderNyxIdAccessToken))
        {
            var token = senderNyxIdAccessToken.Trim();
            var control = LLMControlContextMapper.FromPayload(chatRequest.LlmControl) with
            {
                SenderNyxIdAccessToken = token,
            };
            chatRequest.LlmControl = control.ToPayload();
        }

        cloned.Payload = Any.Pack(chatRequest);
        return cloned;
    }
}
