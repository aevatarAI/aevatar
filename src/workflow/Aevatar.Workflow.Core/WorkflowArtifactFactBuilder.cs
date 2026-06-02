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

        if (TryBuildWorkflowRoleReplyRecordedEvent(envelope, normalizedRunId, out var roleReplyFact))
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
        string runId,
        out WorkflowRoleReplyRecordedEvent evt)
    {
        evt = null!;

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return false;

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published?.StateEvent?.EventData == null ||
            !published.StateEvent.EventData.Is(WorkflowLlmInvocationCompletedEvent.Descriptor))
        {
            return false;
        }

        var completed = published.StateEvent.EventData.Unpack<WorkflowLlmInvocationCompletedEvent>();
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        // Refactor (iter15/cluster-028):
        //   Old pattern: parsed childActorId prefix to derive RoleId via string split.
        //   New principle: role id comes from typed event payload / readmodel; actor id is opaque address only.
        evt = new WorkflowRoleReplyRecordedEvent
        {
            RunId = runId,
            RoleActorId = publisherActorId,
            RoleId = publisherActorId,
            SessionId = completed.SessionId ?? string.Empty,
            Content = completed.Content ?? string.Empty,
            ReasoningContent = completed.ReasoningContent ?? string.Empty,
            ContentEmitted = completed.Success,
        };

        return true;
    }
}
