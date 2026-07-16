using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChatHistory;

public sealed class ChatTurnHistoryDeliveryProjectionSink : IEventSink<WorkflowRunEventEnvelope>
{
    private const string PublisherActorId = "chat-history-delivery-projection-sink";
    private readonly IActorDispatchPort _dispatchPort;
    private readonly string _deliveryActorId;
    private readonly string _deliveryId;
    private readonly string _workflowActorId;
    private readonly string _workflowCommandId;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private bool _completed;

    public ChatTurnHistoryDeliveryProjectionSink(
        IActorDispatchPort dispatchPort,
        string deliveryActorId,
        string deliveryId,
        string workflowActorId,
        string workflowCommandId,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _deliveryActorId = deliveryActorId ?? throw new ArgumentNullException(nameof(deliveryActorId));
        _deliveryId = deliveryId ?? throw new ArgumentNullException(nameof(deliveryId));
        _workflowActorId = workflowActorId ?? throw new ArgumentNullException(nameof(workflowActorId));
        _workflowCommandId = workflowCommandId ?? throw new ArgumentNullException(nameof(workflowCommandId));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Push(WorkflowRunEventEnvelope evt)
    {
        _ = PushAsync(evt, CancellationToken.None);
    }

    public async ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_completed)
            return;

        var terminal = ChatTurnTerminalResult.From(evt);
        if (terminal is null)
            return;

        var command = new ChatTurnHistoryDeliveryTerminalFrameObserved
        {
            DeliveryId = _deliveryId,
            WorkflowActorId = _workflowActorId,
            WorkflowCommandId = _workflowCommandId,
            Status = terminal.Status,
            Text = terminal.Text,
            ErrorCode = terminal.ErrorCode,
            ObservedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, _deliveryActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = _workflowCommandId,
            },
        };

        try
        {
            var admission = await _dispatchPort.DispatchAsync(_deliveryActorId, envelope, ct).ConfigureAwait(false);
            if (!admission.Accepted)
                throw new InvalidOperationException("Chat history terminal delivery continuation was not accepted by the actor dispatch port.");

            _completed = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to dispatch chat history terminal delivery continuation: deliveryId={DeliveryId} workflowActorId={WorkflowActorId} commandId={CommandId}",
                _deliveryId,
                _workflowActorId,
                _workflowCommandId);
            throw;
        }
    }

    public void Complete()
    {
    }

    public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        _completed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed record ChatTurnTerminalResult(
    ChatTurnTerminalStatus Status,
    string Text,
    string ErrorCode)
{
    public static ChatTurnTerminalResult? From(WorkflowRunEventEnvelope frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.EventCase switch
        {
            WorkflowRunEventEnvelope.EventOneofCase.RunFinished => new(
                ChatTurnTerminalStatus.Completed,
                ResolveFinishedText(frame.RunFinished),
                string.Empty),
            WorkflowRunEventEnvelope.EventOneofCase.RunError => new(
                ChatTurnTerminalStatus.Failed,
                string.IsNullOrWhiteSpace(frame.RunError?.Message)
                    ? "Workflow failed."
                    : frame.RunError.Message.Trim(),
                string.IsNullOrWhiteSpace(frame.RunError?.Code)
                    ? "workflow_run_error"
                    : frame.RunError.Code.Trim()),
            WorkflowRunEventEnvelope.EventOneofCase.RunStopped => new(
                ChatTurnTerminalStatus.Stopped,
                string.Empty,
                string.IsNullOrWhiteSpace(frame.RunStopped?.Reason)
                    ? "workflow_run_stopped"
                    : frame.RunStopped.Reason.Trim()),
            _ => null,
        };
    }

    private static string ResolveFinishedText(WorkflowRunFinishedEventPayload? finished)
    {
        if (finished?.Result?.TryUnpack<WorkflowRunResultPayload>(out var result) == true &&
            !string.IsNullOrWhiteSpace(result.Output))
        {
            return result.Output.Trim();
        }

        return string.Empty;
    }
}
