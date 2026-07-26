using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class ValidatedWorkOrderExecutionPort : IWorkOrderExecutionPort
{
    private readonly WorkOrderAssignmentValidator _assignmentValidator;
    private readonly IServiceInvocationPort _serviceInvocationPort;

    public ValidatedWorkOrderExecutionPort(
        WorkOrderAssignmentValidator assignmentValidator,
        IServiceInvocationPort serviceInvocationPort)
    {
        _assignmentValidator = assignmentValidator ?? throw new ArgumentNullException(nameof(assignmentValidator));
        _serviceInvocationPort = serviceInvocationPort ?? throw new ArgumentNullException(nameof(serviceInvocationPort));
    }

    public async Task<WorkOrderExecutionResult> ExecuteAsync(
        WorkOrderExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorkOrderValidatedAssignment assignment;
        try
        {
            assignment = await _assignmentValidator.ValidateAsync(
                request.ScopeId,
                request.TeamId,
                request.MemberId,
                request.PublishedServiceId,
                request.EndpointId,
                ct);
            EnsureAssignmentStillMatches(request, assignment);
        }
        catch (InvalidOperationException ex)
        {
            return Failed(
                "WORK_ORDER_ASSIGNMENT_NOT_DISPATCHABLE",
                ex.Message,
                "studio-work-order-validation",
                request.PublishedServiceId);
        }

        ServiceInvocationAcceptedReceipt receipt;
        try
        {
            receipt = await _serviceInvocationPort.InvokeAsync(
                BuildInvocationRequest(request),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return Failed(
                "WORK_ORDER_SERVICE_INVOCATION_REJECTED",
                ex.Message,
                "gagent-service",
                request.DispatchCommandId);
        }

        if (!string.Equals(receipt.RunId, request.RequestedRunId, StringComparison.Ordinal) ||
            !string.Equals(receipt.CommandId, request.DispatchCommandId, StringComparison.Ordinal) ||
            !string.Equals(receipt.CorrelationId, request.DispatchCommandId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.TargetActorId) ||
            string.IsNullOrWhiteSpace(receipt.DeploymentId))
        {
            return Failed(
                "WORK_ORDER_RUN_IDENTITY_MISMATCH",
                "Service invocation receipt did not preserve the authorized WorkOrder Run link.",
                "gagent-service",
                receipt.RunId);
        }

        return new WorkOrderExecutionResult
        {
            Accepted = new WorkOrderExecutionAccepted
            {
                RunId = receipt.RunId,
                RunActorId = receipt.TargetActorId,
                CommandId = receipt.CommandId,
                CorrelationId = receipt.CorrelationId,
                RevisionId = assignment.ServiceRevisionId,
                DeploymentId = receipt.DeploymentId,
                AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };
    }

    private static ServiceInvocationRequest BuildInvocationRequest(WorkOrderExecutionRequest request)
    {
        if (request.Input?.Chat == null)
            throw new InvalidOperationException("WorkOrder chat input is required for dispatch.");
        if (request.DeadlineAtUtc != null &&
            request.DeadlineAtUtc.ToDateTimeOffset().ToUnixTimeMilliseconds() <= 0)
            throw new InvalidOperationException("WorkOrder execution requires a positive deadline_at_utc.");

        var expiresAtUnixMs = request.DeadlineAtUtc?.ToDateTimeOffset().ToUnixTimeMilliseconds()
                              ?? long.MaxValue;

        var invocationRequest = new ServiceInvocationRequest
        {
            Identity = new ServiceIdentity
            {
                TenantId = request.ScopeId,
                ServiceId = request.PublishedServiceId,
            },
            EndpointId = request.EndpointId,
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = request.Input.Chat.Prompt,
                ScopeId = request.ScopeId,
            }),
            CommandId = request.DispatchCommandId,
            CorrelationId = request.DispatchCommandId,
            RequestedRunId = request.RequestedRunId,
            RunOrigin = WorkflowRunOrigins.WorkOrder,
        };

        switch (request.ImplementationKind)
        {
            case MemberImplementationKindNames.Workflow:
                invocationRequest.WorkflowCompletionNotificationTarget =
                    CreateWorkflowCompletionTarget(request, expiresAtUnixMs);
                break;
            case MemberImplementationKindNames.Script:
            case MemberImplementationKindNames.GAgent:
                invocationRequest.ServiceRunCompletionNotificationTarget =
                    CreateServiceRunCompletionTarget(request, expiresAtUnixMs);
                break;
            default:
                throw new InvalidOperationException(
                    $"WorkOrder implementation kind '{request.ImplementationKind}' is not supported.");
        }

        return invocationRequest;
    }

    private static WorkflowServiceCompletionNotificationTarget CreateWorkflowCompletionTarget(
        WorkOrderExecutionRequest request,
        long expiresAtUnixMs) =>
        new()
        {
            ActorId = request.WorkOrderActorId,
            DeliveryId = request.TerminalDeliveryId,
            ExpiresAtUnixMs = expiresAtUnixMs,
        };

    private static ServiceRunCompletionNotificationTarget CreateServiceRunCompletionTarget(
        WorkOrderExecutionRequest request,
        long expiresAtUnixMs) =>
        new()
        {
            ActorId = request.WorkOrderActorId,
            DeliveryId = request.TerminalDeliveryId,
            ExpiresAtUnixMs = expiresAtUnixMs,
        };

    private static void EnsureAssignmentStillMatches(
        WorkOrderExecutionRequest request,
        WorkOrderValidatedAssignment assignment)
    {
        if (!string.Equals(request.MemberId, assignment.MemberId, StringComparison.Ordinal) ||
            !string.Equals(request.PublishedServiceId, assignment.PublishedServiceId, StringComparison.Ordinal) ||
            !string.Equals(request.WorkflowId, assignment.WorkflowId ?? string.Empty, StringComparison.Ordinal) ||
            !string.Equals(request.ServiceRevisionId, assignment.ServiceRevisionId, StringComparison.Ordinal) ||
            !string.Equals(request.ImplementationKind, assignment.ImplementationKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "WorkOrder assignment changed after authorization; dispatch requires reassignment and a new lifecycle version.");
        }
    }

    private static WorkOrderExecutionResult Failed(
        string code,
        string message,
        string source,
        string referenceId) =>
        new()
        {
            Failed = new WorkOrderExecutionFailed
            {
                Failure = new WorkOrderFailureReference
                {
                    Code = code,
                    Message = message,
                    Source = source,
                    ReferenceId = referenceId,
                },
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };
}
