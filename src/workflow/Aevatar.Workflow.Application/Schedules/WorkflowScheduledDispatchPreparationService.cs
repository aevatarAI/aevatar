using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Schedules;

internal sealed class WorkflowScheduledDispatchPreparationService : IWorkflowScheduledDispatchPreparationService
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest> _envelopeFactory;

    public WorkflowScheduledDispatchPreparationService(
        IWorkflowRunActorResolver actorResolver,
        ICommandEnvelopeFactory<WorkflowChatRunRequest> envelopeFactory)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
    }

    public async Task<ScheduledDispatchPreparation> PrepareAsync(
        WorkflowScheduleConfiguration configuration,
        string commandId,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var request = BuildWorkflowRunRequest(configuration, commandId);
        var actorResolution = await _actorResolver.ResolveOrCreateAsync(request, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Target == null)
        {
            throw new WorkflowScheduleConflictException(
                configuration.ScheduleId,
                $"Workflow schedule '{configuration.ScheduleId}' target could not be prepared: {actorResolution.Error}.");
        }

        var context = new CommandContext(
            actorResolution.Target.ActorId,
            commandId,
            correlationId,
            request.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal));
        var envelope = _envelopeFactory.CreateEnvelope(request, context);
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);

        return new ScheduledDispatchPreparation(
            actorResolution.Target.ActorId,
            envelope,
            envelope.Payload?.TypeUrl ?? string.Empty);
    }

    private static WorkflowChatRunRequest BuildWorkflowRunRequest(
        WorkflowScheduleConfiguration configuration,
        string sessionId)
    {
        var headers = new Dictionary<string, string>(configuration.Headers, StringComparer.Ordinal)
        {
            ["workflow.schedule_id"] = configuration.ScheduleId,
        };

        return new WorkflowChatRunRequest(
            Prompt: configuration.Prompt,
            Source: string.IsNullOrWhiteSpace(configuration.ActorId)
                ? WorkflowChatSource.CatalogWorkflow(configuration.WorkflowName)
                : WorkflowChatSource.DefinitionActor(configuration.ActorId, configuration.WorkflowName),
            SessionId: sessionId,
            Metadata: headers,
            ScopeId: string.IsNullOrWhiteSpace(configuration.ScopeId) ? null : configuration.ScopeId);
    }
}
