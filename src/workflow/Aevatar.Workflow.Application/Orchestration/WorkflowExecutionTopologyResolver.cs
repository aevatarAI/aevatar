using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Orchestration;

public sealed class ActorRuntimeWorkflowExecutionTopologyResolver : IWorkflowExecutionTopologyResolver
{
    public async Task<IReadOnlyList<WorkflowTopologyEdge>> ResolveAsync(
        IActorRuntime runtime,
        string rootActorId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var topology = new List<WorkflowTopologyEdge>();
        if (string.IsNullOrWhiteSpace(rootActorId))
            return topology;

        var root = await runtime.GetAsync(rootActorId);
        if (root == null)
            return topology;

        var visited = new HashSet<string>(StringComparer.Ordinal) { rootActorId };
        var queue = new Queue<string>();
        queue.Enqueue(rootActorId);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var parent = queue.Dequeue();

            var parentActor = await runtime.GetAsync(parent);
            if (parentActor == null)
                continue;

            var children = await parentActor.GetChildrenIdsAsync();
            if (children.Count == 0)
                continue;

            foreach (var child in children)
            {
                topology.Add(new WorkflowTopologyEdge(parent, child));
                if (visited.Add(child))
                    queue.Enqueue(child);
            }
        }

        return topology;
    }
}
