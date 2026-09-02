using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ActorDispatchWorkOrderCommandService : IWorkOrderCommandPort
{
    private const string PublisherId = "aevatar.studio.projection.work-order";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public ActorDispatchWorkOrderCommandService(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public async Task<WorkOrderAcceptedReceipt> CreateAsync(
        string scopeId,
        CreateWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        WorkOrderValidatedAssignment assignment,
        CancellationToken ct = default)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        if (request.TimeoutAtUtc.HasValue && request.TimeoutAtUtc.Value <= requestedAt)
            throw new InvalidOperationException("WorkOrder deadline must be later than the request time.");

        var workOrderId = WorkOrderConventions.BuildWorkOrderId(scopeId, request.DedupKey);
        var command = new CreateWorkOrder
        {
            WorkOrderId = workOrderId,
            DedupKey = request.DedupKey,
            ScopeId = scopeId,
            TeamId = request.TeamId,
            Requester = ToPrincipal(requester),
            MemberId = assignment.MemberId,
            PublishedServiceId = assignment.PublishedServiceId,
            WorkflowId = assignment.WorkflowId ?? string.Empty,
            ServiceRevisionId = assignment.ServiceRevisionId,
            ImplementationKind = assignment.ImplementationKind,
            EndpointId = request.EndpointId,
            Intent = request.Intent,
            Input = ToInput(request.Input),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt),
            ExpectedLifecycleVersion = 0,
        };
        if (request.TimeoutAtUtc.HasValue)
            command.TimeoutAtUtc = Timestamp.FromDateTimeOffset(request.TimeoutAtUtc.Value);

        return await DispatchAsync(
            scopeId,
            workOrderId,
            command,
            "create",
            command.ExpectedLifecycleVersion,
            ct);
    }

    public Task<WorkOrderAcceptedReceipt> ReassignAsync(
        string scopeId,
        string workOrderId,
        ReassignWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        WorkOrderValidatedAssignment assignment,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            workOrderId,
            new ReassignWorkOrder
            {
                WorkOrderId = workOrderId,
                ExpectedLifecycleVersion = request.ExpectedLifecycleVersion,
                RequestedBy = ToPrincipal(requester),
                MemberId = assignment.MemberId,
                PublishedServiceId = assignment.PublishedServiceId,
                WorkflowId = assignment.WorkflowId ?? string.Empty,
                ServiceRevisionId = assignment.ServiceRevisionId,
                ImplementationKind = assignment.ImplementationKind,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "reassign",
            request.ExpectedLifecycleVersion,
            ct);

    public Task<WorkOrderAcceptedReceipt> DispatchAsync(
        string scopeId,
        string workOrderId,
        DispatchWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            workOrderId,
            new DispatchWorkOrder
            {
                WorkOrderId = workOrderId,
                ExpectedLifecycleVersion = request.ExpectedLifecycleVersion,
                RequestedBy = ToPrincipal(requester),
                DispatchCommandId = WorkOrderConventions.BuildDispatchCommandId(workOrderId),
                RequestedRunId = WorkOrderConventions.BuildRequestedRunId(workOrderId),
                TerminalDeliveryId = WorkOrderConventions.BuildTerminalDeliveryId(workOrderId),
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "dispatch",
            request.ExpectedLifecycleVersion,
            ct);

    public Task<WorkOrderAcceptedReceipt> CancelAsync(
        string scopeId,
        string workOrderId,
        CancelWorkOrderRequest request,
        WorkOrderPrincipalContract requester,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            workOrderId,
            new CancelWorkOrder
            {
                WorkOrderId = workOrderId,
                ExpectedLifecycleVersion = request.ExpectedLifecycleVersion,
                RequestedBy = ToPrincipal(requester),
                Reason = request.Reason ?? string.Empty,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "cancel",
            request.ExpectedLifecycleVersion,
            ct);

    private async Task<WorkOrderAcceptedReceipt> DispatchAsync(
        string scopeId,
        string workOrderId,
        IMessage payload,
        string operation,
        long expectedLifecycleVersion,
        CancellationToken ct)
    {
        var commandId = BuildCommandId(operation, workOrderId, expectedLifecycleVersion, payload);
        var actorId = WorkOrderConventions.BuildActorId(scopeId, workOrderId);
        var actor = await _bootstrap.EnsureAsync<WorkOrderGAgent>(actorId, ct);
        var receipt = await _commandDispatch.DispatchAsync(
            actor,
            payload,
            PublisherId,
            commandId,
            commandId,
            commandId,
            ct);
        return new WorkOrderAcceptedReceipt(
            workOrderId,
            receipt.CommandId,
            receipt.CorrelationId,
            WorkOrderCommandStageNames.DispatchAccepted,
            receipt.AckedAt);
    }

    private static WorkOrderServiceInput ToInput(WorkOrderServiceInputContract input)
    {
        var result = new WorkOrderServiceInput
        {
            Chat = new WorkOrderChatInput
            {
                Prompt = input.Chat.Prompt,
            },
        };
        result.InputArtifacts.Add((input.InputArtifacts ?? []).Select(ToArtifact));
        result.DeclaredResultArtifacts.Add((input.DeclaredResultArtifacts ?? []).Select(ToArtifact));
        return result;
    }

    private static WorkOrderArtifactReference ToArtifact(WorkOrderArtifactReferenceContract artifact) =>
        new()
        {
            ArtifactId = artifact.ArtifactId,
            ArtifactKind = artifact.ArtifactKind,
            Uri = artifact.Uri ?? string.Empty,
            RevisionId = artifact.RevisionId ?? string.Empty,
        };

    private static WorkOrderPrincipal ToPrincipal(WorkOrderPrincipalContract principal) =>
        new()
        {
            PrincipalId = principal.PrincipalId,
            PrincipalKind = principal.PrincipalKind,
        };

    private static string BuildCommandId(
        string operation,
        string workOrderId,
        long expectedLifecycleVersion,
        IMessage payload)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(BuildCanonicalCommandBytes(payload)));
        return $"work-order-{operation}-{workOrderId}-v{expectedLifecycleVersion}-{digest}";
    }

    private static byte[] BuildCanonicalCommandBytes(IMessage payload)
    {
        switch (payload)
        {
            case CreateWorkOrder command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                return canonical.ToByteArray();
            }
            case ReassignWorkOrder command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                return canonical.ToByteArray();
            }
            case DispatchWorkOrder command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                return canonical.ToByteArray();
            }
            case CancelWorkOrder command:
            {
                var canonical = command.Clone();
                canonical.RequestedAtUtc = null;
                return canonical.ToByteArray();
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported WorkOrder command payload '{payload.Descriptor.FullName}'.");
        }
    }
}
