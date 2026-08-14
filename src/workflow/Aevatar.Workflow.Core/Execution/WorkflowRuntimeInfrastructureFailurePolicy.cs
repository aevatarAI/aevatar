using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRuntimeInfrastructureFailurePolicy
{
    public static bool IsCommitConsistencyFailure(Exception exception) =>
        exception switch
        {
            EventStoreOptimisticConcurrencyException => true,
            EventStoreVersionDriftException => true,
            CommittedStatePublicationException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(IsCommitConsistencyFailure),
            _ when exception.InnerException is not null =>
                IsCommitConsistencyFailure(exception.InnerException),
            _ => false,
        };
}
