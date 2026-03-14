using System.Runtime.CompilerServices;
using Aevatar.Workflow.Sdk.Contracts;

namespace Aevatar.Workflow.Sdk.Session;

public static class WorkflowClientSessionExtensions
{
    public static async IAsyncEnumerable<WorkflowEvent> StartRunStreamWithTrackingAsync(
        this IAevatarWorkflowClient client,
        ChatRunRequest request,
        RunSessionTracker tracker,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tracker);

        await foreach (var evt in client.StartRunStreamAsync(request, cancellationToken))
        {
            tracker.Track(evt);
            yield return evt;
        }
    }
}
