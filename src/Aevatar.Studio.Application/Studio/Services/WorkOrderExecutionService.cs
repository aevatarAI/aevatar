using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.WorkOrder;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class WorkOrderExecutionService
{
    private readonly IWorkOrderExecutionPort _executionPort;
    private readonly IActorDispatchPort _dispatchPort;

    public WorkOrderExecutionService(
        IWorkOrderExecutionPort executionPort,
        IActorDispatchPort dispatchPort)
    {
        _executionPort = executionPort ?? throw new ArgumentNullException(nameof(executionPort));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task ExecuteAsync(
        WorkOrderExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IMessage continuation;
        try
        {
            var result = await _executionPort.ExecuteAsync(request.Clone(), ct).ConfigureAwait(false);
            continuation = result.ResultCase switch
            {
                WorkOrderExecutionResult.ResultOneofCase.Accepted =>
                    BuildAcceptedContinuation(request, result.Accepted),
                WorkOrderExecutionResult.ResultOneofCase.Failed =>
                    BuildFailedContinuation(request, result.Failed),
                _ => throw new InvalidOperationException(
                    "WorkOrder execution returned no accepted or failed result."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            continuation = BuildUnexpectedFailureContinuation(request, ex);
        }

        var operationId =
            $"work-order-execution-result:{request.WorkOrderId}:{request.DispatchCommandId}";
        await _dispatchPort.DispatchAsync(
                request.WorkOrderActorId,
                BuildEnvelope(request.WorkOrderActorId, operationId, continuation),
                ct)
            .ConfigureAwait(false);
    }

    private static WorkOrderExecutionAcceptedContinuation BuildAcceptedContinuation(
        WorkOrderExecutionRequest request,
        WorkOrderExecutionAccepted accepted) =>
        new()
        {
            WorkOrderId = request.WorkOrderId,
            DispatchCommandId = request.DispatchCommandId,
            RequestedRunId = request.RequestedRunId,
            Accepted = accepted.Clone(),
        };

    private static WorkOrderExecutionFailedContinuation BuildFailedContinuation(
        WorkOrderExecutionRequest request,
        WorkOrderExecutionFailed failed) =>
        new()
        {
            WorkOrderId = request.WorkOrderId,
            DispatchCommandId = request.DispatchCommandId,
            RequestedRunId = request.RequestedRunId,
            Failed = failed.Clone(),
        };

    private static WorkOrderExecutionFailedContinuation BuildUnexpectedFailureContinuation(
        WorkOrderExecutionRequest request,
        Exception exception) =>
        BuildFailedContinuation(
            request,
            new WorkOrderExecutionFailed
            {
                Failure = new WorkOrderFailureReference
                {
                    Code = "WORK_ORDER_EXECUTION_UNEXPECTED_FAILURE",
                    Message = $"WorkOrder execution failed unexpectedly ({exception.GetType().Name}).",
                    Source = WorkOrderConventions.ExecutionWorkerPublisherActorId,
                    ReferenceId = request.DispatchCommandId,
                },
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });

    private static EventEnvelope BuildEnvelope(
        string targetActorId,
        string operationId,
        IMessage continuation) =>
        new()
        {
            Id = operationId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(continuation),
            Route = EnvelopeRouteSemantics.CreateDirect(
                WorkOrderConventions.ExecutionWorkerPublisherActorId,
                targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = operationId,
            },
        };
}
