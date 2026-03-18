using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal static class WorkflowArtifactFactBuilder
{
    public static bool TryBuild(
        EventEnvelope envelope,
        string actorId,
        string? stateRunId,
        out IMessage artifactFact)
    {
        artifactFact = null!;
        if (envelope.Payload == null)
            return false;

        var normalizedRunId = string.IsNullOrWhiteSpace(stateRunId)
            ? WorkflowRunIdNormalizer.Normalize(actorId)
            : WorkflowRunIdNormalizer.Normalize(stateRunId);

        if (TryBuildWorkflowRoleReplyRecordedEvent(envelope, actorId, normalizedRunId, out var roleReplyFact))
        {
            artifactFact = roleReplyFact;
            return true;
        }

        if (envelope.Payload.Is(WorkflowCompletedEvent.Descriptor))
            return false;

        if (envelope.Payload.Is(StepRequestEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<StepRequestEvent>();
            return true;
        }

        if (envelope.Payload.Is(StepCompletedEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<StepCompletedEvent>();
            return true;
        }

        if (envelope.Payload.Is(WorkflowSuspendedEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WorkflowSuspendedEvent>();
            return true;
        }

        if (envelope.Payload.Is(WaitingForSignalEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WaitingForSignalEvent>();
            return true;
        }

        if (envelope.Payload.Is(WorkflowSignalBufferedEvent.Descriptor))
        {
            artifactFact = envelope.Payload.Unpack<WorkflowSignalBufferedEvent>();
            return true;
        }

        return false;
    }

    private static bool TryBuildWorkflowRoleReplyRecordedEvent(
        EventEnvelope envelope,
        string actorId,
        string runId,
        out WorkflowRoleReplyRecordedEvent evt)
    {
        evt = null!;

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return false;

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published?.StateEvent?.EventData == null ||
            !published.StateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
        {
            return false;
        }

        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        if (!IsRoleChildActor(actorId, publisherActorId))
            return false;

        var completed = published.StateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>();
        evt = new WorkflowRoleReplyRecordedEvent
        {
            RunId = runId,
            RoleActorId = publisherActorId,
            RoleId = ResolveRoleId(actorId, publisherActorId),
            SessionId = completed.SessionId ?? string.Empty,
            Content = completed.Content ?? string.Empty,
            ReasoningContent = completed.ReasoningContent ?? string.Empty,
            Prompt = completed.Prompt ?? string.Empty,
            ContentEmitted = completed.ContentEmitted,
        };

        foreach (var toolCall in completed.ToolCalls)
        {
            evt.ToolCalls.Add(new WorkflowRoleReplyToolCall
            {
                ToolName = toolCall.ToolName ?? string.Empty,
                CallId = toolCall.CallId ?? string.Empty,
            });
        }

        return true;
    }

    private static bool IsRoleChildActor(string actorId, string childActorId) =>
        !string.IsNullOrWhiteSpace(childActorId) &&
        childActorId.StartsWith(actorId + ":", StringComparison.Ordinal);

    private static string ResolveRoleId(string actorId, string childActorId)
    {
        if (!IsRoleChildActor(actorId, childActorId))
            return childActorId ?? string.Empty;

        return childActorId[(actorId.Length + 1)..];
    }
}
