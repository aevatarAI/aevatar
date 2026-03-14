using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal static class WorkflowQueryRouteConventions
{
    public const string ActorBindingReplyStreamPrefix = WorkflowQueryChannels.ActorBindingReplyStreamPrefix;

    public static string BuildActorBindingTimeoutMessage(string requestId) =>
        $"Timeout waiting for workflow actor binding query response. request_id={requestId}";
}
