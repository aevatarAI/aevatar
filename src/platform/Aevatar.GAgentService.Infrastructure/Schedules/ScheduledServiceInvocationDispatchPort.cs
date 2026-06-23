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

        var request = WithScheduleId(
            await BuildInvocationRequestAsync(dispatch, ct),
            dispatch.ScheduleId);
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
        if (dispatch.Auth?.ScopeOwnerNyxId == null)
        {
            var durableToken = dispatch.Auth?.DurableSenderBearerToken;
            if (!string.IsNullOrWhiteSpace(durableToken))
            {
                return EnrichChatPayload(
                    dispatch.Request,
                    dispatch.Headers,
                    new ExchangedCredential(
                        CredentialRole.Sender,
                        NormalizeNyxIdAccessToken(durableToken, ToErrorSubject(CredentialRole.Sender))),
                    dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential);
            }
        }

        var exchange = await ExchangeCredentialAsync(dispatch, ct);
        if (exchange == null)
        {
            return EnrichChatPayload(
                dispatch.Request,
                dispatch.Headers,
                credential: null,
                projectNyxIdAccessTokenToWorkflowCallerCredential:
                    dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential);
        }

        if (!exchange.Result.Succeeded)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(exchange.Result.Error)
                ? $"Scheduled service invocation {ToErrorSubject(exchange.Role)} NyxID credential exchange failed."
                : exchange.Result.Error.Trim());
        }

        return EnrichChatPayload(
            dispatch.Request,
            dispatch.Headers,
            new ExchangedCredential(
                exchange.Role,
                NormalizeNyxIdAccessToken(exchange.Result.AccessToken, ToErrorSubject(exchange.Role))),
            dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential);
    }

    private static ServiceInvocationRequest WithScheduleId(ServiceInvocationRequest request, string? scheduleId)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
            return request;

        if (string.Equals(request.ScheduleId, scheduleId.Trim(), StringComparison.Ordinal))
            return request;

        var cloned = request.Clone();
        cloned.ScheduleId = scheduleId.Trim();
        return cloned;
    }

    private async Task<CredentialExchange?> ExchangeCredentialAsync(
        ScheduledServiceInvocationDispatchRequest dispatch,
        CancellationToken ct)
    {
        if (dispatch.Auth == null)
            return null;
        if (dispatch.Auth.ScopeOwnerNyxId != null)
        {
            var result = await ExchangeScopeOwnerNyxIdAsync(
                dispatch.Auth.ScopeOwnerNyxId,
                dispatch.Request.Identity,
                ct);
            return new CredentialExchange(CredentialRole.ScopeOwner, result);
        }
        if (dispatch.Auth.SenderNyxId != null)
        {
            var result = await ExchangeSenderNyxIdAsync(dispatch.Auth.SenderNyxId, ct);
            return new CredentialExchange(CredentialRole.Sender, result);
        }

        return null;
    }

    private async Task<ScheduledServiceInvocationCredentialExchangeResult> ExchangeScopeOwnerNyxIdAsync(
        ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource source,
        ServiceIdentity serviceIdentity,
        CancellationToken ct) =>
        await _credentialExchangePort.IssueScopeOwnerNyxIdAsync(source, serviceIdentity, ct);

    private async Task<ScheduledServiceInvocationCredentialExchangeResult> ExchangeSenderNyxIdAsync(
        ScheduledServiceInvocationNyxIdCredentialSource source,
        CancellationToken ct) =>
        await _credentialExchangePort.IssueSenderNyxIdAsync(source, ct);

    private static ServiceInvocationRequest EnrichChatPayload(
        ServiceInvocationRequest request,
        IReadOnlyDictionary<string, string>? headers,
        ExchangedCredential? credential,
        bool projectNyxIdAccessTokenToWorkflowCallerCredential)
    {
        if ((headers == null || headers.Count == 0) && credential == null)
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

        if (credential != null)
        {
            var token = credential.AccessToken;
            var existingControl = LLMControlContextMapper.FromPayload(chatRequest.LlmControl);
            var control = existingControl with
            {
                NyxIdAccessToken = credential.Role == CredentialRole.ScopeOwner
                    ? token
                    : existingControl.NyxIdAccessToken,
                NyxIdOrgToken = credential.Role == CredentialRole.ScopeOwner
                    ? token
                    : existingControl.NyxIdOrgToken,
                SenderNyxIdAccessToken = credential.Role == CredentialRole.Sender
                    ? token
                    : existingControl.SenderNyxIdAccessToken,
            };
            chatRequest.LlmControl = control.ToPayload();
            if (projectNyxIdAccessTokenToWorkflowCallerCredential)
                chatRequest.ConnectorHttpAuthorization = $"Bearer {token}";
        }

        cloned.Payload = Any.Pack(chatRequest);
        return cloned;
    }

    private static string NormalizeNyxIdAccessToken(string? accessToken, string credentialSubject)
    {
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(accessToken);
        if (parsed.IsMissing)
            throw new InvalidOperationException($"Scheduled service invocation {credentialSubject} NyxID credential exchange returned an empty access token.");
        if (parsed.IsInvalid)
            throw new InvalidOperationException($"Scheduled service invocation {credentialSubject} NyxID credential exchange returned an invalid access token.");

        return parsed.NormalizedBearerToken!;
    }

    private enum CredentialRole
    {
        Sender,
        ScopeOwner,
    }

    private sealed record CredentialExchange(
        CredentialRole Role,
        ScheduledServiceInvocationCredentialExchangeResult Result);

    private sealed record ExchangedCredential(CredentialRole Role, string AccessToken);

    private static string ToErrorSubject(CredentialRole role) =>
        role == CredentialRole.ScopeOwner ? "scope owner" : "sender";
}
