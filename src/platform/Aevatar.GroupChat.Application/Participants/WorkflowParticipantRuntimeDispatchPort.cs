using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GroupChat.Application.Participants;

public sealed class WorkflowParticipantRuntimeDispatchPort : IParticipantRuntimeDispatcher
{
    private readonly ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _dispatchService;

    public WorkflowParticipantRuntimeDispatchPort(
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> dispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
    }

    public bool CanDispatch(Aevatar.GroupChat.Abstractions.Queries.GroupParticipantRuntimeBindingSnapshot binding) =>
        binding.TargetKind == GroupParticipantRuntimeTargetKind.Workflow &&
        binding.WorkflowTarget != null;

    public async Task<ParticipantRuntimeDispatchResult?> DispatchAsync(
        ParticipantRuntimeDispatchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Binding.WorkflowTarget;
        if (target == null)
            return null;

        var sessionId = GroupParticipantRuntimeSessionId.Build(
            request.GroupId,
            request.ThreadId,
            request.TriggerMessage.TopicId,
            request.ParticipantAgentId,
            request.SourceEventId,
            request.TriggerMessage.MessageId);

        var dispatch = await _dispatchService.DispatchAsync(
            new WorkflowChatRunRequest(
                Prompt: request.TriggerMessage.Text,
                WorkflowName: string.IsNullOrWhiteSpace(target.WorkflowName) ? null : target.WorkflowName,
                ActorId: string.IsNullOrWhiteSpace(target.DefinitionActorId) ? null : target.DefinitionActorId,
                SessionId: sessionId,
                WorkflowYamls: null,
                Metadata: BuildAnnotations(request),
                ScopeId: string.IsNullOrWhiteSpace(target.ScopeId) ? null : target.ScopeId),
            ct);
        if (!dispatch.Succeeded || dispatch.Receipt == null)
            return null;

        return new ParticipantRuntimeDispatchResult(
            ParticipantRuntimeBackendKind.Workflow,
            ParticipantRuntimeCompletionMode.AsyncObserved,
            dispatch.Receipt.ActorId,
            sessionId,
            GroupParticipantReplyMessageIds.FromSource(request.ParticipantAgentId, request.SourceEventId));
    }

    private static IReadOnlyDictionary<string, string> BuildAnnotations(ParticipantRuntimeDispatchRequest request)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["group_id"] = request.GroupId,
            ["thread_id"] = request.ThreadId,
            ["topic_id"] = request.TriggerMessage.TopicId,
            ["message_id"] = request.TriggerMessage.MessageId,
            ["participant_agent_id"] = request.ParticipantAgentId,
            ["source_event_id"] = request.SourceEventId,
            ["timeline_cursor"] = request.TimelineCursor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["state_version"] = request.SourceStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
