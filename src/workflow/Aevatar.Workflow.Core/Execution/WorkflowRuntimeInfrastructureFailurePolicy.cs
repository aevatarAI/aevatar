using Aevatar.Foundation.Core.EventSourcing;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRuntimeInfrastructureFailurePolicy
{
    public static bool IsCommittedStatePublicationFailure(Exception exception) =>
        exception switch
        {
            CommittedStatePublicationException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(IsCommittedStatePublicationFailure),
            _ when exception.InnerException is not null =>
                IsCommittedStatePublicationFailure(exception.InnerException),
            _ => false,
        };
}
