using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;

public sealed class WorkflowRunBackgroundDeliveryRegistrationPort
    : IWorkflowRunBackgroundDeliveryRegistrationPort
{
    private const string PublisherActorId = "workflow-run-background-delivery-registration";
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowRunBackgroundDeliveryRegistrationPort> _logger;

    public WorkflowRunBackgroundDeliveryRegistrationPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ILogger<WorkflowRunBackgroundDeliveryRegistrationPort> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowRunBackgroundDeliveryReceipt> RegisterAsync(
        WorkflowRunBackgroundDeliveryRegistration registration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var actorId = string.IsNullOrWhiteSpace(registration.DeliveryId)
            ? WorkflowRunDeliveryActorIds.FromWorkflowCommand(registration.WorkflowActorId, registration.WorkflowCommandId)
            : registration.DeliveryId.Trim();
        if (!await _actorRuntime.ExistsAsync(actorId).ConfigureAwait(false))
            await _actorRuntime.CreateAsync<WorkflowRunDeliveryGAgent>(actorId, ct).ConfigureAwait(false);

        var command = new WorkflowRunDeliveryStartRequested
        {
            DeliveryId = actorId,
            WorkflowActorId = registration.WorkflowActorId ?? string.Empty,
            WorkflowRunId = registration.WorkflowRunId ?? string.Empty,
            WorkflowCommandId = registration.WorkflowCommandId ?? string.Empty,
            WorkflowCorrelationId = registration.WorkflowCorrelationId ?? string.Empty,
            StreamTopic = registration.StreamTopic ?? string.Empty,
            ChannelPlatform = registration.ChannelPlatform ?? string.Empty,
            ReplyMessageId = registration.ReplyMessageId ?? string.Empty,
            PlatformMessageId = registration.PlatformMessageId ?? string.Empty,
            BotAgentKeyId = registration.BotAgentKeyId ?? string.Empty,
            RegistrationScopeId = registration.RegistrationScopeId ?? string.Empty,
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(registration.WorkflowCorrelationId)
                    ? registration.WorkflowCommandId
                    : registration.WorkflowCorrelationId,
            },
        };
        envelope.EnsureRuntime().EnsureDeduplication().OperationId = $"workflow-run-delivery-start:{actorId}";

        _logger.LogInformation(
            "Registering workflow run background delivery: deliveryActorId={DeliveryActorId} workflowActorId={WorkflowActorId} commandId={CommandId}",
            actorId,
            registration.WorkflowActorId,
            registration.WorkflowCommandId);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);

        return new WorkflowRunBackgroundDeliveryReceipt
        {
            DeliveryActorId = actorId,
            WorkflowActorId = registration.WorkflowActorId ?? string.Empty,
            WorkflowRunId = registration.WorkflowRunId ?? string.Empty,
            WorkflowCommandId = registration.WorkflowCommandId ?? string.Empty,
            WorkflowCorrelationId = registration.WorkflowCorrelationId ?? string.Empty,
            StreamTopic = registration.StreamTopic ?? string.Empty,
            ChannelPlatform = registration.ChannelPlatform ?? string.Empty,
            ReplyMessageId = registration.ReplyMessageId ?? string.Empty,
            PlatformMessageId = registration.PlatformMessageId ?? string.Empty,
            BotAgentKeyId = registration.BotAgentKeyId ?? string.Empty,
            RegistrationScopeId = registration.RegistrationScopeId ?? string.Empty,
        };
    }
}
