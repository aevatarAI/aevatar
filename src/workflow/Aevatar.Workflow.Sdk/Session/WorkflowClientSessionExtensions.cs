using System.Runtime.CompilerServices;
using Aevatar.Workflow.Sdk.Contracts;

namespace Aevatar.Workflow.Sdk.Session;

public static class WorkflowClientSessionExtensions
{
    // Refactor (iter82/cluster-082-workflow-sdk-library-await-cancellation):
    //   Old pattern: SDK awaits without library-safe ConfigureAwait/WithCancellation; OperationCanceledException wrapped as Transport failure
    //   New principle: library awaits ConfigureAwait(false), async-enumerable WithCancellation, preserve OperationCanceledException
    public static async IAsyncEnumerable<WorkflowEvent> StartRunStreamWithTrackingAsync(
        this IAevatarWorkflowClient client,
        ChatRunRequest request,
        RunSessionTracker tracker,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tracker);

        await foreach (var evt in client.StartRunStreamAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            tracker.Track(evt);
            yield return evt;
        }
    }
}
