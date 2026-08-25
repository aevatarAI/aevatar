using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal sealed record NyxIdChatControlAcceptedReceipt(
    string ActorId,
    string RequestId,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AcceptedAt);

internal interface INyxIdChatControlCommandPort
{
    Task<NyxIdChatControlAcceptedReceipt> DispatchStopAsync(
        NyxIdChatStopCommand command,
        CancellationToken ct = default);

    Task<NyxIdChatControlAcceptedReceipt> DispatchSteeringAsync(
        NyxIdChatSteeringCommand command,
        CancellationToken ct = default);

    Task<NyxIdChatControlAcceptedReceipt> DispatchRetryAsync(
        NyxIdChatRetryStepCommand command,
        CancellationToken ct = default);

    Task<NyxIdChatControlAcceptedReceipt> DispatchSkipAsync(
        NyxIdChatSkipStepCommand command,
        CancellationToken ct = default);

    Task<NyxIdChatControlAcceptedReceipt> DispatchInputResolveAsync(
        NyxIdChatInputResolveCommand command,
        CancellationToken ct = default);

    Task<NyxIdChatControlAcceptedReceipt> DispatchApprovalResolveAsync(
        NyxIdChatApprovalResolveCommand command,
        CancellationToken ct = default);

    Task<NyxIdChatControlAcceptedReceipt> DispatchCanaryEffectFaultArmAsync(
        NyxIdChatCanaryEffectFaultArmCommand command,
        CancellationToken ct = default);
}

internal sealed class NyxIdChatControlCommandPort : INyxIdChatControlCommandPort
{
    private readonly IActorDispatchPort _dispatchPort;

    public NyxIdChatControlCommandPort(IActorDispatchPort dispatchPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task<NyxIdChatControlAcceptedReceipt> DispatchStopAsync(
        NyxIdChatStopCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(command, command?.StopRequestId, command?.ConversationActorId, command?.CommandId,
            command?.CorrelationId, ct);

    public Task<NyxIdChatControlAcceptedReceipt> DispatchSteeringAsync(
        NyxIdChatSteeringCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(command, command?.SteeringId, command?.ConversationActorId, command?.CommandId,
            command?.CorrelationId, ct);

    public Task<NyxIdChatControlAcceptedReceipt> DispatchRetryAsync(
        NyxIdChatRetryStepCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(command, command?.RetryRequestId, command?.ConversationActorId, command?.CommandId,
            command?.CorrelationId, ct);

    public Task<NyxIdChatControlAcceptedReceipt> DispatchSkipAsync(
        NyxIdChatSkipStepCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(command, command?.SkipRequestId, command?.ConversationActorId, command?.CommandId,
            command?.CorrelationId, ct);

    public Task<NyxIdChatControlAcceptedReceipt> DispatchInputResolveAsync(
        NyxIdChatInputResolveCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(command, command?.RequestId, command?.ConversationActorId, command?.CommandId,
            command?.CorrelationId, ct);

    public Task<NyxIdChatControlAcceptedReceipt> DispatchApprovalResolveAsync(
        NyxIdChatApprovalResolveCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(command, command?.RequestId, command?.ConversationActorId, command?.CommandId,
            command?.CorrelationId, ct);

    public Task<NyxIdChatControlAcceptedReceipt> DispatchCanaryEffectFaultArmAsync(
        NyxIdChatCanaryEffectFaultArmCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(command, command?.ArmId, command?.ConversationActorId, command?.CommandId,
            command?.CorrelationId, ct);

    private async Task<NyxIdChatControlAcceptedReceipt> DispatchAsync(
        IMessage? command,
        string? requestId,
        string? actorId,
        string? commandId,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalizedActorId = NormalizeRequired(actorId, nameof(actorId));
        var normalizedRequestId = NormalizeRequired(requestId, nameof(requestId));
        var normalizedCommandId = NormalizeRequired(commandId, nameof(commandId));
        var normalizedCorrelationId = NormalizeRequired(correlationId, nameof(correlationId));
        var envelope = new EventEnvelope
        {
            Id = normalizedCommandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = normalizedActorId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = normalizedCorrelationId,
            },
        };
        var admission = await _dispatchPort
            .DispatchAsync(normalizedActorId, envelope, ct)
            .ConfigureAwait(false);
        if (!admission.Accepted)
            throw new InvalidOperationException("NyxIdChat control dispatch was not accepted.");

        return new NyxIdChatControlAcceptedReceipt(
            admission.ActorId,
            normalizedRequestId,
            admission.CommandId,
            admission.CorrelationId,
            admission.AckedAt);
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return normalized;
    }
}
