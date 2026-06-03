using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Schedules;

internal sealed class WorkflowScheduledDispatchAdapterPort : IActorDispatchPort
{
    private readonly IActorDispatchPort _inner;
    private readonly Func<IWorkflowRunActorResolver> _workflowRunActorResolver;
    private readonly Func<ICommandEnvelopeFactory<WorkflowChatRunRequest>> _workflowChatEnvelopeFactory;
    private readonly Func<IServiceInvocationPort?> _serviceInvocationPortResolver;

    public WorkflowScheduledDispatchAdapterPort(
        IActorDispatchPort inner,
        IWorkflowRunActorResolver workflowRunActorResolver,
        ICommandEnvelopeFactory<WorkflowChatRunRequest> workflowChatEnvelopeFactory,
        Func<IServiceInvocationPort?>? serviceInvocationPortResolver = null)
        : this(
            inner,
            () => workflowRunActorResolver,
            () => workflowChatEnvelopeFactory,
            serviceInvocationPortResolver)
    {
        ArgumentNullException.ThrowIfNull(workflowRunActorResolver);
        ArgumentNullException.ThrowIfNull(workflowChatEnvelopeFactory);
    }

    public WorkflowScheduledDispatchAdapterPort(
        IActorDispatchPort inner,
        Func<IWorkflowRunActorResolver> workflowRunActorResolver,
        Func<ICommandEnvelopeFactory<WorkflowChatRunRequest>> workflowChatEnvelopeFactory,
        Func<IServiceInvocationPort?>? serviceInvocationPortResolver = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _workflowRunActorResolver = workflowRunActorResolver ?? throw new ArgumentNullException(nameof(workflowRunActorResolver));
        _workflowChatEnvelopeFactory = workflowChatEnvelopeFactory ?? throw new ArgumentNullException(nameof(workflowChatEnvelopeFactory));
        _serviceInvocationPortResolver = serviceInvocationPortResolver ?? (() => null);
    }

    public async Task<DispatchAdmission> DispatchAsync(
        string actorId,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.Equals(
                actorId.Trim(),
                ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                StringComparison.Ordinal) &&
            envelope.Payload?.TryUnpack<ServiceInvocationRequest>(out var serviceInvocationRequest) == true)
        {
            var serviceInvocationPort = _serviceInvocationPortResolver();
            if (serviceInvocationPort == null)
                throw new InvalidOperationException("Service invocation scheduled dispatch adapter is not registered.");

            var receipt = await serviceInvocationPort.InvokeAsync(serviceInvocationRequest, ct);
            return new DispatchAdmission(
                true,
                receipt.CommandId,
                DateTimeOffset.UtcNow,
                receipt.TargetActorId,
                receipt.CorrelationId);
        }

        if (!string.Equals(
                actorId.Trim(),
                WorkflowScheduledDispatchAdapterConventions.TargetActorId,
                StringComparison.Ordinal) ||
            envelope.Payload == null ||
            !envelope.Payload.TryUnpack<WorkflowScheduledDispatchStartRequest>(out var request))
        {
            return await _inner.DispatchAsync(actorId, envelope, ct);
        }

        var runEnvelope = await BuildWorkflowRunEnvelopeAsync(request, envelope, ct);
        return await _inner.DispatchAsync(runEnvelope.TargetActorId, runEnvelope.Envelope, ct);
    }

    private async Task<PreparedWorkflowRunEnvelope> BuildWorkflowRunEnvelopeAsync(
        WorkflowScheduledDispatchStartRequest request,
        EventEnvelope scheduledEnvelope,
        CancellationToken ct)
    {
        var commandId = string.IsNullOrWhiteSpace(scheduledEnvelope.Id)
            ? Guid.NewGuid().ToString("N")
            : scheduledEnvelope.Id.Trim();

        var requestHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            requestHeaders[key.Trim()] = value.Trim();
        }

        var workflowRequest = new WorkflowChatRunRequest(
            Prompt: request.Prompt,
            Source: string.IsNullOrWhiteSpace(request.SourceActorId)
                ? WorkflowChatSource.CatalogWorkflow(request.WorkflowName)
                : WorkflowChatSource.DefinitionActor(request.SourceActorId, request.WorkflowName),
            SessionId: commandId,
            Metadata: requestHeaders,
            ScopeId: string.IsNullOrWhiteSpace(request.ScopeId) ? null : request.ScopeId);

        var actorResolution = await _workflowRunActorResolver().ResolveOrCreateAsync(workflowRequest, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Target == null)
        {
            throw new InvalidOperationException(
                $"Workflow schedule '{request.ScheduleId}' target could not be prepared: {actorResolution.Error}.");
        }

        var context = new CommandContext(
            actorResolution.Target.ActorId,
            commandId,
            scheduledEnvelope.Propagation?.CorrelationId ?? commandId,
            requestHeaders);
        var envelope = _workflowChatEnvelopeFactory().CreateEnvelope(workflowRequest, context);
        envelope.Id = commandId;
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(
            scheduledEnvelope.Route?.PublisherActorId ?? WorkflowScheduledDispatchAdapterConventions.TargetActorId,
            actorResolution.Target.ActorId);
        envelope.Runtime = null;
        var propagation = envelope.EnsurePropagation();
        propagation.CorrelationId = context.CorrelationId;

        return new PreparedWorkflowRunEnvelope(actorResolution.Target.ActorId, envelope);
    }

    private sealed record PreparedWorkflowRunEnvelope(
        string TargetActorId,
        EventEnvelope Envelope);
}
