namespace Aevatar.Workflow.Abstractions;

public static class WorkflowQueryChannels
{
    public const string ActorBindingPublisherId = "workflow.query.actor-binding";
    public const string ActorBindingReplyStreamPrefix = ActorBindingPublisherId + ".reply";
}
