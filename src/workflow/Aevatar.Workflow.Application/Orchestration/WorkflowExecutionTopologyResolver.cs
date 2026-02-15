using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Application.Orchestration;

public sealed class ActorRuntimeWorkflowExecutionTopologyResolver : IWorkflowExecutionTopologyResolver
{
    public async Task<IReadOnlyList<WorkflowExecutionTopologyEdge>> ResolveAsync(
        IActorRuntime runtime,
        string rootActorId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var allActors = await runtime.GetAllAsync();
        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var actor in allActors)
        {
            var parent = await actor.GetParentIdAsync();
            if (string.IsNullOrWhiteSpace(parent))
                continue;

            if (!childrenByParent.TryGetValue(parent, out var children))
            {
                children = [];
                childrenByParent[parent] = children;
            }

            children.Add(actor.Id);
        }

        var topology = new List<WorkflowExecutionTopologyEdge>();
        if (string.IsNullOrWhiteSpace(rootActorId))
            return topology;

        var visited = new HashSet<string>(StringComparer.Ordinal) { rootActorId };
        var queue = new Queue<string>();
        queue.Enqueue(rootActorId);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var parent = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parent, out var children))
                continue;

            foreach (var child in children)
            {
                topology.Add(new WorkflowExecutionTopologyEdge(parent, child));
                if (visited.Add(child))
                    queue.Enqueue(child);
            }
        }

        return topology;
    }
}
